﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using static BlueprintExplorer.BlueprintViewer;

namespace BlueprintExplorer
{
    public partial class Form1 : BubbleprintsForm {
        
        private BlueprintDB db => BlueprintDB.Instance;
        private static bool dark;
        bool Good => initialize?.IsCompleted ?? false;

        public BlueprintViewer NewBlueprintViewer(int index = -1)
        {
            var viewer = new BlueprintViewer();
            if (Dark)
            {
                BubbleTheme.DarkenControls(viewer);
            }

            viewer.View.Font = BlueprintFont;
            viewer.View.LinkFont = LinkFont;
            var page = new TabPage("<empty>");

            viewer.OnBlueprintShown += bp =>
            {
                page.Text = "     " + bp.Name + "     ";
            };

            viewer.OnOpenExternally += bp =>
            {
                DoOpenInEditor(bp);
            };

            viewer.OnLinkOpenNewTab += bp =>
            {
                var viewer = NewBlueprintViewer();
                viewer.ShowBlueprint(bp, ShowFlags.F_ClearHistory | ShowFlags.F_UpdateHistory);
                blueprintViews.SelectedIndex = blueprintViews.TabCount - 1;
            };

            viewer.OnClose += () =>
            {
                if (blueprintViews.TabCount > 1)
                    blueprintViews.TabPages.Remove(page);
            };

            page.Controls.Add(viewer);
            viewer.Dock = DockStyle.Fill;
            blueprintViews.TabPages.Add(page);
            return viewer;
        }

        public class BubbleNotification
        {
            public string Message;
            public Action Action;
            public string ActionText;

            public bool Complete;
        }

        private NotificationsView notificationsView = new();
        private List<BubbleNotification> pendingNotifications = new();
        private ToolTip notificationTooltip = new();

        public void AddNotification(BubbleNotification notification)
        {
            pendingNotifications.Add(notification);
            ValidateNotifications();
        }

        public void ValidateNotifications()
        {
            pendingNotifications = pendingNotifications.Where(n => !n.Complete).ToList();

            if (pendingNotifications.Count == 0)
            {
                controlBar.ColumnStyles[^1].Width = 0;
            }
            else
            {
                controlBar.ColumnStyles[^1].Width = 64;
                notifications.Text = pendingNotifications.Count.ToString();
                notificationTooltip.SetToolTip(notifications, pendingNotifications.Count.ToString() + " pending notifications");
            }

        }

        private static long ParseVersion(string v)
        {
            var c = v.Split('.');
            return int.Parse(c[0]) * 65536 + int.Parse(c[1]) * 256 + int.Parse(c[2]);
        }

        public Form1() {
            var env = Environment.GetEnvironmentVariable("BubbleprintsTheme");
            Dark = env?.Equals("dark") ?? false;
            Dark |= BubblePrints.Settings.DarkMode;

            long version = ParseVersion(Application.ProductVersion);

            if (BubblePrints.Settings.CheckForUpdates)
            {
                Task.Run(async () =>
                {
                    using WebClient client = new();
                    client.Headers.Add("User-Agent", "BubblePrints");
                    var raw = await client.DownloadStringTaskAsync("https://api.github.com/repos/factubsio/BubblePrints/releases/latest");
                    return JsonSerializer.Deserialize<JsonElement>(raw);
                }).ContinueWith(t =>
                {
                    var json = t.Result;
                    if (json.TryGetProperty("tag_name", out var tag))
                    {
                        long latest = ParseVersion(tag.GetString()[1..]);
                        if (latest > version)
                        {
                            AddNotification(new()
                            {
                                Message = "An update is available (" + tag + ")",
                                Action = () => Process.Start("explorer", json.GetProperty("assets")[0].GetProperty("browser_download_url").GetString()),
                                ActionText = "Download now",
                            });
                        }
                    }
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }

            InitializeComponent();
            NewBlueprintViewer();
            omniSearch.TextChanged += OmniSearch_TextChanged;
            resultsGrid.CellClick += ResultsGrid_CellClick;

            InstallReadline(omniSearch);

            controlBar.ColumnStyles[^1].Width = 0;

            Color bgColor = omniSearch.BackColor;
            resultsGrid.RowHeadersVisible = false;

            availableVersions.Enabled = false;

            notifications.Click += (sender, evt) =>
            {
                notificationsView.Show(this, pendingNotifications);
                ValidateNotifications();
            };


            settingsButton.Click += (sender, evt) =>
            {
                new SettingsView().ShowDialog();
            };

            BubblePrints.SetWrathPath();

            if (BubblePrints.TryGetWrathPath(out var wrathPath))
            {
                BubblePrints.Wrath = Assembly.LoadFrom(Path.Combine(wrathPath, "Wrath_Data", "Managed", "Assembly-CSharp.dll"));
            }

            resultsGrid.AllowUserToResizeRows = false;

            blueprintViews.DrawMode = TabDrawMode.OwnerDrawFixed;
            blueprintViews.DrawItem += (sender, e) =>
            {
                var g = e.Graphics;
                g.FillRectangle(new SolidBrush(resultsGrid.BackColor), e.Bounds);
                var textBounds = e.Bounds;
                textBounds.Inflate(-2, -2);
                var title = blueprintViews.TabPages[e.Index].Text;
                int halfSize = (int)(g.MeasureString(title, Font).Width / 2);
                int center = textBounds.Left + textBounds.Width / 2;
                textBounds.X = center - halfSize;
                g.DrawString(title, Font, new SolidBrush(resultsGrid.ForeColor), textBounds);
            };

            if (Dark)
            {
                resultsGrid.EnableHeadersVisualStyles = false;
                BubbleTheme.DarkenControls(omniSearch, resultsGrid, splitContainer1, panel1, settingsButton, blueprintViews, helpButton);
                BubbleTheme.DarkenStyles(resultsGrid.ColumnHeadersDefaultCellStyle, resultsGrid.DefaultCellStyle);

                Invalidate();
            }



            omniSearch.Enabled = false;
            resultsGrid.Enabled = false;

            if (SeasonalOverlay.InSeason)
            {
                BubbleTheme.SeasonControls(omniSearch, panel1, settingsButton, resultsGrid, helpButton);
                BubbleTheme.SeasonStyles(resultsGrid.ColumnHeadersDefaultCellStyle, resultsGrid.DefaultCellStyle);
                SeasonalOverlay.Install(resultsGrid);
            }

            var loadType = BlueprintDB.Instance.GetLoadType();

            var loadString = loadType switch
            {
                BlueprintDB.GoingToLoad.FromLocalFile => "LOADING (debug)",
                BlueprintDB.GoingToLoad.FromCache => "LOADING (local)",
                BlueprintDB.GoingToLoad.FromWeb => "DOWNLOADING",
                BlueprintDB.GoingToLoad.FromNewImport => "IMPORTING",
                _ => throw new Exception(),
            };

            var progress = new BlueprintDB.ConnectionProgress();

            initialize = Task.Run(() => BlueprintDB.Instance.TryConnect(progress));
            initialize.ContinueWith(b => {
                omniSearch.Enabled = true;
                resultsGrid.Enabled = true;
                omniSearch.Text = "";
                omniSearch.Select();
                ShowBlueprint(BlueprintDB.Instance.Blueprints.Values.First(), ShowFlags.F_UpdateHistory);

                foreach (var v in BlueprintDB.Instance.Available)
                    availableVersions.Items.Add(v);
                availableVersions.SelectedIndex = availableVersions.Items.Count - 1;
                availableVersions.Enabled = true;
            }, TaskScheduler.FromCurrentSynchronizationContext());


            new Thread(() => {
                string plane = $"{loadString}-🛬";
                const int frames = 90;

                while (true) {
                    for (int frame = 0; frame < frames; frame++) {
                        if (Good)
                            return;

                        if (omniSearch.Visible) {
                            omniSearch.Invoke(new Action(() => {
                                if (!Good) {
                                    omniSearch.Text = plane.PadLeft(plane.Length + frame) + $"{progress.Status}";
                                }
                            }));
                        }
                        Thread.Sleep(33);
                    }
                }
            }).Start();

        }

        private void BlueprintView_OnLinkClicked(string link)
        {
            throw new NotImplementedException();
        }

        private void ResultsGrid_Paint(object sender, PaintEventArgs e)
        {
            e.Graphics.FillRectangle(Brushes.Yellow, 0, 0, 100, 100);
        }


        private void DoOpenInEditor(BlueprintHandle blueprint)
        {
            if (blueprint == null)
                return;
            var userLocalFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BubblePrints", "cache");
            if (!Directory.Exists(userLocalFolder))
                Directory.CreateDirectory(userLocalFolder);

            string fileToOpen = Path.Combine(userLocalFolder, blueprint.Name + "_" + blueprint.GuidText + ".json");

            //if (!File.Exists(fileToOpen))
            {
                using var stream = File.CreateText(fileToOpen);
                TextExporter.Export(stream, blueprint);
            }
            var editor = BubblePrints.Settings.Editor;
            if (editor == null || !File.Exists(editor))
                editor = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
            string[] args = BubblePrints.Settings.ExternalEditorTemplate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "{blueprint}")
                    args[i] = fileToOpen;
            }
            Process.Start(editor, args);
        }

        private void ResultsGrid_CellClick(object sender, DataGridViewCellEventArgs e) {
            ShowSelected();
        }

        private DateTime lastChange = DateTime.MinValue;
        private TimeSpan debounceTime = TimeSpan.FromSeconds(1.5);

        int lastFinished = 0;
        private CancellationTokenSource finishingFirst;
        private CancellationTokenSource finishingLast;
        private Task<List<BlueprintHandle>> overlappedSearch;

        private void SetResults(List<BlueprintHandle> results, CancellationTokenSource cancellation, int matchBuffer)
        {
            if (cancellation == finishingFirst)
                finishingFirst = null;
            if (cancellation == finishingLast)
                finishingLast = null;

            lastFinished = matchBuffer;
            BlueprintDB.UnlockBuffer(matchBuffer);
            resultsCache = results;
            var oldRowCount = resultsGrid.Rows.Count;
            var newRowCount = resultsCache.Count;
            if (newRowCount > oldRowCount)
                resultsGrid.Rows.Add(newRowCount - oldRowCount);
            else {
                resultsGrid.Rows.Clear();
                if (newRowCount > 0)
                    resultsGrid.Rows.Add(newRowCount);
            }
            resultsGrid.Invalidate();
        }

        private void InvalidateResults() {
            CancellationTokenSource cancellation = new();

            int matchBuffer = 0;

            if (finishingLast != null && finishingLast != finishingFirst)
            {
                finishingLast.Cancel();
                finishingLast.Token.WaitHandle.WaitOne();
                BlueprintDB.UnlockBuffer(1);
            }

            finishingLast = cancellation;

            if (finishingFirst == null)
            {
                matchBuffer = 0;
                finishingFirst = cancellation;
            }
            else
            {
                matchBuffer = 1;
            }


            Task<List<BlueprintHandle>> search;

            if (matchBuffer == 1)
            {
                overlappedSearch = db.SearchBlueprintsAsync(Search, cancellation.Token, matchBuffer);
                search = overlappedSearch;
            }
            else
            {
                search = db.SearchBlueprintsAsync(Search, cancellation.Token, matchBuffer);
            }

            search.ContinueWith(task =>
            {
                if (!task.IsCanceled && !cancellation.IsCancellationRequested)
                    this.Invoke((Action<List<BlueprintHandle>, CancellationTokenSource, int>)SetResults, task.Result, cancellation, matchBuffer);
            });

         }
        private void OmniSearch_TextChanged(object sender, EventArgs e) {
            if (!Good)
                return;
            InvalidateResults();
        }

        private string Search => omniSearch.Text;

        public static bool Dark { get => dark; set => dark = value; }

        private static readonly char[] wordSeparators =
        {
            ' ',
            '.',
            '/',
            ':',
        };
        private static void KillForwardLine(TextBox box) {
            var here = box.SelectionStart;
            string Search = box.Text;
            if (box.SelectionLength == 0) {
                if (here > 0)
                    box.Text = Search.Substring(0, here);
                else
                    box.Text = "";
                box.Select(Search.Length, 0);

            }

        }

        private static void KillBackLine(TextBox box) {
            var here = box.SelectionStart;
            string Search = box.Text;
            if (box.SelectionLength == 0) {
                if (here < Search.Length)
                    box.Text = Search[here..];
                else
                    box.Text = "";

            }

        }

        private static void KillBackWord(TextBox box) {
            var here = box.SelectionStart;
            string Search = box.Text;
            if (box.SelectionLength == 0) {
                if (here == 0)
                    return;

                while (here > 0 && Search[here - 1] == ' ')
                    here--;

                var killTo = Search.LastIndexOfAny(wordSeparators, here - 1);
                if (killTo == -1)
                    killTo = 0;

                string newSearch;

                if (killTo > 0)
                    newSearch = Search.Substring(0, killTo + 1);
                else
                    newSearch = "";

                if (here < Search.Length) {
                    newSearch += Search[here..];
                }

                box.Text = newSearch;
                box.SelectionStart = killTo + 1;
            }
        }

        public static void InstallReadline(TextBox box)
        {
            List<string> history = new();
            int historyIndex = -1;

            box.KeyDown += (sender, e) =>
            {
                if (e.Control)
                {
                    switch (e.KeyCode)
                    {
                        case Keys.W:
                            KillBackWord(box);
                            break;
                        case Keys.K:
                            KillForwardLine(box);
                            break;
                        case Keys.U:
                            KillBackLine(box);
                            break;
                        case Keys.E:
                            box.Select(box.Text.Length, 0);
                            break;
                        case Keys.A:
                            box.Select(0, 0);
                            e.Handled = true;
                            e.SuppressKeyPress = true;
                            break;
                        case Keys.N:
                            int next = historyIndex + 1;
                            if (next < history.Count)
                            {
                                historyIndex = next;
                                box.Text = history[historyIndex];
                            }
                            e.Handled = true;
                            e.SuppressKeyPress = true;
                            break;
                        case Keys.P:
                            if (history.Count > 0 && historyIndex > 0)
                            {
                                historyIndex--;
                                box.Text = history[historyIndex];
                            }
                            e.Handled = true;
                            e.SuppressKeyPress = true;
                            break;
                    }
                }
                else if (e.KeyCode == Keys.Return || e.KeyCode == Keys.Enter)
                {
                    history.Add(box.Text);
                    historyIndex = history.Count - 1;
                }
            };
        }

        private void omniSearch_KeyDown(object sender, KeyEventArgs e) {
            if (e.KeyCode == Keys.Return || e.KeyCode == Keys.Enter) {
                if (resultsCache.Count > 0) {
                    ShowSelected();
                }
            }
            else if (e.KeyCode == Keys.Up) {
                if (resultsCache.Count > 1) {
                    int row = resultsGrid.SelectedRow() - 1;
                    if (row >= 0 && row < resultsCache.Count) {
                        resultsGrid.Rows[row].Selected = true;
                        resultsGrid.CurrentCell = resultsGrid[0, row];
                        resultsGrid.CurrentCell.ToolTipText = "";
                    }
                }
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Down) {
                if (resultsCache.Count > 1) {
                    int row = resultsGrid.SelectedRow() + 1;
                    if (row < resultsCache.Count) {
                        resultsGrid.Rows[row].Selected = true;
                        resultsGrid.CurrentCell = resultsGrid[0, row];
                        resultsGrid.CurrentCell.ToolTipText = "";
                    }
                }
                e.Handled = true;
                e.SuppressKeyPress = true;
            }

        }

        private void ShowSelected() {
            if (TryGetSelected(out var row))
            {
                ShowBlueprint(resultsCache[row], ShowFlags.F_ClearHistory | ShowFlags.F_UpdateHistory);
            }
        }


        private bool TryGetSelected(out int row) {
            row = resultsGrid.SelectedRow();
            return row >= 0 && row < resultsCache.Count;
        }



        private void ShowBlueprint(BlueprintHandle bp, ShowFlags flags) {
            if (flags.UpdateHistory() && BubblePrints.Settings.AlwaysOpenInEditor)
                DoOpenInEditor(bp);

            if (blueprintViews.SelectedTab.Controls[0] is BlueprintViewer bpView)
                bpView.ShowBlueprint(bp, flags);

        }

        private List<BlueprintHandle> resultsCache = new();
        private Task<bool> initialize;

        private void dataGridView1_CellValueNeeded(object sender, DataGridViewCellValueEventArgs e) {
            var row = e.RowIndex;
            if ((resultsCache?.Count ?? 0) == 0) {
                e.Value = "...";
                return;
            }

            if (row >= resultsCache.Count)
                return;

            e.Value = e.ColumnIndex switch
            {
                0 => resultsCache[row].Name,
                1 => resultsCache[row].TypeName,
                2 => resultsCache[row].Namespace,
                3 => resultsCache[row].Score(lastFinished).ToString(),
                4 => resultsCache[row].GuidText,
                _ => "<error>",
            };
        }


        private void omniSearch_TextChanged_1(object sender, EventArgs e) {

        }

        private void label1_Click(object sender, EventArgs e) {

        }

        private void resultsGrid_CellContentClick(object sender, DataGridViewCellEventArgs e) {

        }

        private void textBox1_TextChanged(object sender, EventArgs e)
        {

        }

        private HelpView helpView;

        private void helpButton_Click(object sender, EventArgs e)
        {
            helpView ??= new();
            helpView.Disposed += (sender, e) => helpView = null;
            if (helpView.Visible)
                helpView.BringToFront();
            else
                helpView.Show();
        }
    }
}
