using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace ShadowingPlayer
{
    public sealed class MainForm : Form
    {
        private const double MaxSentencePlaybackSeconds = 20.0;
        private const int MinimumSentenceDurationMilliseconds = 50;
        private const string WhisperModelCacheRoot = @"E:\Models";
        private static readonly Color CurrentSentenceColor = Color.FromArgb(0, 102, 204);

        private readonly Panel videoHost = new Panel();
        private readonly Button openButton = new Button();
        private readonly Button playPauseButton = new Button();
        private readonly Button backButton = new Button();
        private readonly Button forwardButton = new Button();
        private readonly Button stopButton = new Button();
        private readonly Button transcribeButton = new Button();
        private readonly Button subtitlesButton = new Button();
        private readonly Button sentenceModeButton = new Button();
        private readonly Button previousSentenceButton = new Button();
        private readonly Button nextSentenceButton = new Button();
        private readonly Button repeatSentenceButton = new Button();
        private readonly Button waveformButton = new Button();
        private readonly Button sentenceStartEarlierButton = new Button();
        private readonly Button sentenceStartLaterButton = new Button();
        private readonly Button sentenceEndEarlierButton = new Button();
        private readonly Button sentenceEndLaterButton = new Button();
        private readonly ComboBox speedCombo = new ComboBox();
        private readonly ComboBox whisperModelCombo = new ComboBox();
        private readonly ComboBox transcriptModelCombo = new ComboBox();
        private readonly NumericUpDown timingStepInput = new NumericUpDown();
        private readonly Label whisperModelStatusLabel = new Label();
        private readonly Label transcriptModelStatusLabel = new Label();
        private readonly Label statusLabel = new Label();
        private readonly Panel playerPanel = new Panel();
        private readonly RichTextBox transcriptBox = new RichTextBox();
        private readonly WaveformPanel waveformPanel = new WaveformPanel();
        private readonly Timer waveformTimer = new Timer();
        private readonly List<TranscriptSegment> transcriptSegments = new List<TranscriptSegment>();
        private MpvController mpv;
        private Process activeTranscriptionProcess;
        private string currentVideoPath;
        private string currentTranscriptPath;
        private string activeTranscriptionOutputPath;
        private float[] waveformPeaks = new float[0];
        private double waveformDurationSeconds;
        private bool subtitlesVisible = true;
        private bool sentenceModeEnabled;
        private bool waveformVisible;
        private bool waveformTimerBusy;
        private bool isClosingConfirmed;
        private bool isShutdownInProgress;
        private bool transcriptModelSelectionBusy;
        private int currentSentenceIndex = -1;
        private string currentTranscriptModel;

        public MainForm()
        {
            Text = "Shadowing Player";
            MinimumSize = new Size(1140, 620);
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;

            var toolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 84,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(8, 8, 8, 6),
                WrapContents = true,
            };

            openButton.Text = "Open";
            openButton.Width = 88;
            openButton.Click += async delegate { await OpenVideoAsync(); };

            backButton.Text = "-5s";
            backButton.Width = 64;
            backButton.Click += async delegate { await SendAsync(controller => controller.SeekAsync(-5)); };

            playPauseButton.Text = "Play/Pause";
            playPauseButton.Width = 104;
            playPauseButton.Click += async delegate
            {
                if (sentenceModeEnabled)
                {
                    await PlayCurrentSentenceAsync();
                }
                else
                {
                    await SendAsync(controller => controller.TogglePauseAsync());
                }
            };

            forwardButton.Text = "+5s";
            forwardButton.Width = 64;
            forwardButton.Click += async delegate { await SendAsync(controller => controller.SeekAsync(5)); };

            stopButton.Text = "Stop";
            stopButton.Width = 72;
            stopButton.Click += async delegate
            {
                sentenceModeEnabled = false;
                UpdateSentenceModeButtons();
                await SendAsync(controller => controller.DisableSentenceModeAsync());
                await SendAsync(controller => controller.StopAsync());
            };

            transcribeButton.Text = "Transcribe";
            transcribeButton.Width = 104;
            transcribeButton.Click += async delegate { await TranscribeCurrentVideoAsync(); };

            whisperModelCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            whisperModelCombo.Width = 96;
            whisperModelCombo.Items.AddRange(new object[] { "base", "small", "medium", "large-v3" });
            whisperModelCombo.SelectedIndex = 1;
            whisperModelCombo.SelectedIndexChanged += delegate { UpdateWhisperModelStatus(); };

            transcriptModelCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            transcriptModelCombo.Width = 96;
            transcriptModelCombo.Enabled = false;
            transcriptModelCombo.SelectedIndexChanged += async delegate { await LoadSelectedTranscriptModelAsync(); };

            whisperModelStatusLabel.AutoSize = false;
            whisperModelStatusLabel.Width = 112;
            whisperModelStatusLabel.Height = 28;
            whisperModelStatusLabel.TextAlign = ContentAlignment.MiddleLeft;

            transcriptModelStatusLabel.AutoSize = false;
            transcriptModelStatusLabel.Width = 140;
            transcriptModelStatusLabel.Height = 28;
            transcriptModelStatusLabel.TextAlign = ContentAlignment.MiddleLeft;
            transcriptModelStatusLabel.Text = "Transcript: none";

            subtitlesButton.Text = "Subtitles: On";
            subtitlesButton.Width = 112;
            subtitlesButton.Click += async delegate { await ToggleSubtitlesAsync(); };

            sentenceModeButton.Text = "Sentence: Off";
            sentenceModeButton.Width = 112;
            sentenceModeButton.Enabled = false;
            sentenceModeButton.Click += async delegate { await ToggleSentenceModeAsync(); };

            previousSentenceButton.Text = "Prev sentence";
            previousSentenceButton.Width = 112;
            previousSentenceButton.Enabled = false;
            previousSentenceButton.Click += async delegate { await MoveSentenceAsync(-1); };

            repeatSentenceButton.Text = "Repeat sentence";
            repeatSentenceButton.Width = 124;
            repeatSentenceButton.Enabled = false;
            repeatSentenceButton.Click += async delegate { await PlayCurrentSentenceAsync(); };

            waveformButton.Text = "Waveform: Off";
            waveformButton.Width = 116;
            waveformButton.Enabled = false;
            waveformButton.Click += async delegate { await ToggleWaveformAsync(); };

            nextSentenceButton.Text = "Next sentence";
            nextSentenceButton.Width = 112;
            nextSentenceButton.Enabled = false;
            nextSentenceButton.Click += async delegate { await MoveSentenceAsync(1); };

            sentenceStartEarlierButton.Text = "Start -";
            sentenceStartEarlierButton.Width = 72;
            sentenceStartEarlierButton.Enabled = false;
            sentenceStartEarlierButton.Click += async delegate { await AdjustSentenceBoundaryAsync(true, -1); };

            sentenceStartLaterButton.Text = "Start +";
            sentenceStartLaterButton.Width = 72;
            sentenceStartLaterButton.Enabled = false;
            sentenceStartLaterButton.Click += async delegate { await AdjustSentenceBoundaryAsync(true, 1); };

            sentenceEndEarlierButton.Text = "End -";
            sentenceEndEarlierButton.Width = 72;
            sentenceEndEarlierButton.Enabled = false;
            sentenceEndEarlierButton.Click += async delegate { await AdjustSentenceBoundaryAsync(false, -1); };

            sentenceEndLaterButton.Text = "End +";
            sentenceEndLaterButton.Width = 72;
            sentenceEndLaterButton.Enabled = false;
            sentenceEndLaterButton.Click += async delegate { await AdjustSentenceBoundaryAsync(false, 1); };

            timingStepInput.DecimalPlaces = 2;
            timingStepInput.Increment = 0.05M;
            timingStepInput.Minimum = 0.01M;
            timingStepInput.Maximum = 5M;
            timingStepInput.Value = 0.10M;
            timingStepInput.Width = 64;
            timingStepInput.Enabled = false;

            speedCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            speedCombo.Width = 90;
            speedCombo.Items.AddRange(new object[] { "0.75x", "1.0x", "1.25x", "1.5x", "2.0x" });
            speedCombo.SelectedIndex = 1;
            speedCombo.SelectedIndexChanged += async delegate { await ChangeSpeedAsync(); };

            statusLabel.AutoSize = false;
            statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            statusLabel.Width = 360;
            statusLabel.Height = 28;
            statusLabel.Text = "Open a video to start.";

            toolbar.Controls.Add(openButton);
            toolbar.Controls.Add(backButton);
            toolbar.Controls.Add(playPauseButton);
            toolbar.Controls.Add(forwardButton);
            toolbar.Controls.Add(stopButton);
            toolbar.Controls.Add(transcribeButton);
            toolbar.Controls.Add(new Label { Text = "Model", AutoSize = true, Padding = new Padding(8, 6, 0, 0) });
            toolbar.Controls.Add(whisperModelCombo);
            toolbar.Controls.Add(whisperModelStatusLabel);
            toolbar.Controls.Add(new Label { Text = "Transcript", AutoSize = true, Padding = new Padding(8, 6, 0, 0) });
            toolbar.Controls.Add(transcriptModelCombo);
            toolbar.Controls.Add(transcriptModelStatusLabel);
            toolbar.Controls.Add(subtitlesButton);
            toolbar.Controls.Add(sentenceModeButton);
            toolbar.Controls.Add(previousSentenceButton);
            toolbar.Controls.Add(repeatSentenceButton);
            toolbar.Controls.Add(nextSentenceButton);
            toolbar.Controls.Add(waveformButton);
            toolbar.Controls.Add(sentenceStartEarlierButton);
            toolbar.Controls.Add(sentenceStartLaterButton);
            toolbar.Controls.Add(sentenceEndEarlierButton);
            toolbar.Controls.Add(sentenceEndLaterButton);
            toolbar.Controls.Add(new Label { Text = "Step", AutoSize = true, Padding = new Padding(8, 6, 0, 0) });
            toolbar.Controls.Add(timingStepInput);
            toolbar.Controls.Add(new Label { Text = "Speed", AutoSize = true, Padding = new Padding(8, 6, 0, 0) });
            toolbar.Controls.Add(speedCombo);
            toolbar.Controls.Add(statusLabel);

            videoHost.Dock = DockStyle.Fill;
            videoHost.BackColor = Color.Black;

            waveformPanel.Dock = DockStyle.Bottom;
            waveformPanel.Visible = false;
            waveformPanel.BoundaryChanged += async delegate(object sender, WaveformBoundaryChangedEventArgs args)
            {
                await SetSentenceBoundaryFromWaveformAsync(args.IsStartBoundary, TimeSpan.FromSeconds(args.Seconds));
            };
            waveformPanel.BoundaryDragStarted += async delegate
            {
                await PauseForBoundaryAdjustmentAsync();
            };
            waveformPanel.SeekRequested += async delegate(object sender, WaveformSeekEventArgs args)
            {
                await SeekFromWaveformAsync(TimeSpan.FromSeconds(args.Seconds));
            };

            waveformTimer.Interval = 150;
            waveformTimer.Tick += async delegate { await UpdateWaveformPlaybackAsync(); };

            playerPanel.Dock = DockStyle.Fill;
            playerPanel.Controls.Add(videoHost);
            playerPanel.Controls.Add(waveformPanel);

            transcriptBox.Dock = DockStyle.Right;
            transcriptBox.Width = 360;
            transcriptBox.Multiline = true;
            transcriptBox.ReadOnly = true;
            transcriptBox.ScrollBars = RichTextBoxScrollBars.Vertical;
            transcriptBox.Font = new Font(FontFamily.GenericSansSerif, 10f);
            transcriptBox.BackColor = SystemColors.Window;
            transcriptBox.BorderStyle = BorderStyle.Fixed3D;
            transcriptBox.DetectUrls = false;
            transcriptBox.Text = "Transcript will appear here.";

            Controls.Add(playerPanel);
            Controls.Add(transcriptBox);
            Controls.Add(toolbar);
            FormClosing += MainForm_FormClosing;
            UpdateWhisperModelStatus();
        }

        private async void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (isClosingConfirmed || isShutdownInProgress)
            {
                return;
            }

            e.Cancel = true;

            if (IsTranscriptionRunning())
            {
                var result = MessageBox.Show(
                    this,
                    "A transcription is still running.\r\n\r\nChoose Yes to quit now and cancel it, or No to keep waiting.",
                    "Transcription In Progress",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);

                if (result != DialogResult.Yes)
                {
                    statusLabel.Text = "Transcription is still running.";
                    return;
                }

                await CancelActiveTranscriptionAsync();
            }

            isShutdownInProgress = true;
            try
            {
                await ShutdownAsync();
                isClosingConfirmed = true;
                Close();
            }
            finally
            {
                isShutdownInProgress = false;
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if ((keyData & (Keys.Control | Keys.Alt)) != 0)
            {
                return base.ProcessCmdKey(ref msg, keyData);
            }

            switch (keyData & Keys.KeyCode)
            {
                case Keys.W:
                    RunKeyboardCommand(ToggleSentenceModeAsync);
                    return true;
                case Keys.A:
                    RunKeyboardCommand(delegate { return MoveSentenceAsync(-1); });
                    return true;
                case Keys.S:
                    RunKeyboardCommand(PlayCurrentSentenceAsync);
                    return true;
                case Keys.D:
                    RunKeyboardCommand(delegate { return MoveSentenceAsync(1); });
                    return true;
                default:
                    return base.ProcessCmdKey(ref msg, keyData);
            }
        }

        private void RunKeyboardCommand(Func<Task> command)
        {
            BeginInvoke(new Action(async delegate
            {
                try
                {
                    await command();
                }
                catch (Exception ex)
                {
                    statusLabel.Text = ex.Message;
                }
            }));
        }

        private async Task OpenVideoAsync()
        {
            using (var dialog = new OpenFileDialog
            {
                Filter = "Video files|*.mp4;*.mkv;*.webm;*.avi;*.mov;*.m4v|All files|*.*",
                Title = "Open video",
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                try
                {
                    await EnsurePlayerAsync();

                    try
                    {
                        await PreparePlayerForNewVideoAsync(dialog.FileName);
                    }
                    catch
                    {
                        await RestartPlayerAsync();
                        await PreparePlayerForNewVideoAsync(dialog.FileName);
                    }

                    currentVideoPath = dialog.FileName;
                    currentTranscriptPath = null;
                    waveformPeaks = new float[0];
                    waveformDurationSeconds = 0;
                    waveformPanel.SetWaveform(waveformPeaks, waveformDurationSeconds);
                    waveformPanel.SetMessage("Waveform not loaded.");
                    transcriptSegments.Clear();
                    currentSentenceIndex = -1;
                    currentTranscriptModel = null;
                    subtitlesVisible = true;
                    subtitlesButton.Text = "Subtitles: On";
                    UpdateSentenceModeButtons();
                    transcriptBox.Text = "Transcript will appear here.";
                    statusLabel.Text = Path.GetFileName(dialog.FileName);

                    RefreshTranscriptModelChoices();
                    await LoadCachedTranscriptAsync();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Could not start mpv", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    statusLabel.Text = "Install mpv.exe or set SHADOWING_MPV_PATH.";
                }
            }
        }

        private async Task PreparePlayerForNewVideoAsync(string path)
        {
            sentenceModeEnabled = false;
            UpdateSentenceModeButtons();
            await mpv.DisableSentenceModeAsync();
            await mpv.LoadSentenceSegmentsAsync("[]");
            await mpv.LoadFileAsync(path);
        }

        private async Task LoadCachedTranscriptAsync()
        {
            var srtPath = GetTranscriptCachePath(currentVideoPath, GetSelectedWhisperModel());
            if (!File.Exists(srtPath))
            {
                RefreshTranscriptModelChoices();
                return;
            }

            await Task.Delay(500);
            await LoadTranscriptFromSrtAsync(srtPath, "Loaded cached transcript.", GetSelectedWhisperModel());
        }

        private async Task TranscribeCurrentVideoAsync()
        {
            if (mpv == null || string.IsNullOrWhiteSpace(currentVideoPath))
            {
                statusLabel.Text = "Open a video first.";
                return;
            }

            var srtPath = GetTranscriptCachePath(currentVideoPath, GetSelectedWhisperModel());

            try
            {
                transcribeButton.Enabled = false;

                if (File.Exists(srtPath))
                {
                    await LoadTranscriptFromSrtAsync(srtPath, "Loaded cached transcript.", GetSelectedWhisperModel());
                    return;
                }

                var pythonPath = ResolveAppPath(@".venv\Scripts\python.exe");
                var scriptPath = ResolveAppPath("transcribe_test.py");

                if (!File.Exists(pythonPath))
                {
                    statusLabel.Text = "Python venv not found.";
                    MessageBox.Show(this, pythonPath, "Python venv not found", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                if (!File.Exists(scriptPath))
                {
                    statusLabel.Text = "Transcription script not found.";
                    MessageBox.Show(this, scriptPath, "Transcription script not found", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                var modelName = GetSelectedWhisperModel();
                transcriptBox.Text = "Transcribing with faster-whisper model " + modelName + ". The first run may download the model...";
                statusLabel.Text = "Transcribing with " + modelName + "...";

                var transcriptionOutput = await RunTranscriptionAsync(pythonPath, scriptPath, currentVideoPath, srtPath, modelName);
                var deviceSummary = ExtractTranscriptionDeviceSummary(transcriptionOutput);
                UpdateWhisperModelStatus();
                await LoadTranscriptFromSrtAsync(srtPath, "Transcript saved and loaded as subtitles.", modelName);

                if (!string.IsNullOrWhiteSpace(deviceSummary))
                {
                    statusLabel.Text = "Transcript saved and loaded as subtitles. " + deviceSummary;
                }
            }
            catch (Exception ex)
            {
                if (!isClosingConfirmed && !IsDisposed)
                {
                    transcriptBox.Text = ex.Message;
                    statusLabel.Text = "Transcription failed.";
                }
            }
            finally
            {
                if (!isClosingConfirmed && !IsDisposed)
                {
                    transcribeButton.Enabled = true;
                }
            }
        }

        private async Task LoadTranscriptFromSrtAsync(string srtPath, string message, string transcriptModel)
        {
            var previousSentenceIndex = currentSentenceIndex;
            var playbackPosition = await TryGetPlaybackPositionAsync();

            transcriptSegments.Clear();
            transcriptSegments.AddRange(ParseSrtFile(srtPath));
            ApplyOrPromoteMasterTranscript(currentVideoPath, transcriptModel, transcriptSegments);
            WriteSrtFile(srtPath, transcriptSegments);

            currentTranscriptPath = srtPath;
            currentTranscriptModel = transcriptModel;
            currentSentenceIndex = ResolveSentenceIndexForTranscriptReload(playbackPosition, previousSentenceIndex);
            transcriptBox.Text = BuildTranscriptText(message + " Model: " + transcriptModel + ".", transcriptSegments);

            await mpv.AddSubtitleAsync(srtPath);
            await Task.Delay(100);
            subtitlesVisible = true;
            subtitlesButton.Text = "Subtitles: On";
            await mpv.SetSubtitleVisibilityAsync(true);
            await LoadSentenceSegmentsIntoPlayerAsync();
            UpdateWaveformSentences();
            if (playbackPosition.HasValue)
            {
                waveformPanel.SetPlaybackPosition(
                    GetWaveformDisplayPosition(playbackPosition.Value).TotalSeconds,
                    currentSentenceIndex);
            }
            RefreshTranscriptModelChoices();
            UpdateSentenceModeButtons();
            SelectCurrentSentenceText();
            UpdateTranscriptModelStatus();
            statusLabel.Text = message + " " + transcriptSegments.Count + " sentences.";
        }

        private async Task ToggleSubtitlesAsync()
        {
            if (mpv == null)
            {
                statusLabel.Text = "Open a video first.";
                return;
            }

            subtitlesVisible = !subtitlesVisible;
            subtitlesButton.Text = subtitlesVisible ? "Subtitles: On" : "Subtitles: Off";

            try
            {
                await mpv.SetSubtitleVisibilityAsync(subtitlesVisible);
            }
            catch (Exception ex)
            {
                statusLabel.Text = ex.Message;
            }
        }

        private async Task ToggleSentenceModeAsync()
        {
            try
            {
                if (mpv == null || transcriptSegments.Count == 0)
                {
                    statusLabel.Text = "Transcribe the video first.";
                    return;
                }

                sentenceModeEnabled = !sentenceModeEnabled;
                UpdateSentenceModeButtons();

                if (sentenceModeEnabled)
                {
                    await LoadSentenceSegmentsIntoPlayerAsync();
                    await StartSentenceModeAtCurrentPositionAsync();
                }
                else if (mpv != null)
                {
                    await mpv.DisableSentenceModeAsync();
                    await mpv.SetPauseAsync(true);
                    statusLabel.Text = "Sentence mode off.";
                }
            }
            catch (Exception ex)
            {
                sentenceModeEnabled = false;
                UpdateSentenceModeButtons();
                statusLabel.Text = ex.Message;
            }
        }

        private async Task LoadSelectedTranscriptModelAsync()
        {
            if (transcriptModelSelectionBusy || string.IsNullOrWhiteSpace(currentVideoPath))
            {
                return;
            }

            var selectedModel = transcriptModelCombo.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(selectedModel) || selectedModel == currentTranscriptModel)
            {
                return;
            }

            var srtPath = GetTranscriptCachePath(currentVideoPath, selectedModel);
            if (!File.Exists(srtPath))
            {
                UpdateTranscriptModelStatus();
                return;
            }

            try
            {
                await LoadTranscriptFromSrtAsync(srtPath, "Loaded cached transcript.", selectedModel);
            }
            catch (Exception ex)
            {
                statusLabel.Text = ex.Message;
            }
        }

        private async Task ToggleWaveformAsync()
        {
            if (string.IsNullOrWhiteSpace(currentVideoPath))
            {
                statusLabel.Text = "Open a video first.";
                return;
            }

            waveformVisible = !waveformVisible;
            waveformPanel.Visible = waveformVisible;
            waveformButton.Text = waveformVisible ? "Waveform: On" : "Waveform: Off";

            if (!waveformVisible)
            {
                waveformTimer.Stop();
                return;
            }

            await EnsureWaveformLoadedAsync();
            UpdateWaveformSentences();
            waveformTimer.Start();
            await UpdateWaveformPlaybackAsync();
        }

        private async Task EnsureWaveformLoadedAsync()
        {
            if (waveformPeaks.Length > 0 && waveformDurationSeconds > 0)
            {
                return;
            }

            var cachePath = GetWaveformCachePath(currentVideoPath);
            try
            {
                if (File.Exists(cachePath) && TryLoadWaveformCache(cachePath, out waveformPeaks, out waveformDurationSeconds))
                {
                    waveformPanel.SetWaveform(waveformPeaks, waveformDurationSeconds);
                    statusLabel.Text = "Loaded cached waveform.";
                    return;
                }

                waveformPanel.SetMessage("Generating waveform...");
                statusLabel.Text = "Generating waveform...";

                var result = await Task.Run(new Func<WaveformData>(() => GenerateWaveform(currentVideoPath)));
                waveformPeaks = result.Peaks;
                waveformDurationSeconds = result.DurationSeconds;
                SaveWaveformCache(cachePath, waveformPeaks, waveformDurationSeconds);
                waveformPanel.SetWaveform(waveformPeaks, waveformDurationSeconds);
                statusLabel.Text = "Waveform ready.";
            }
            catch (Exception ex)
            {
                waveformPeaks = new float[0];
                waveformDurationSeconds = 0;
                waveformPanel.SetMessage(ex.Message);
                statusLabel.Text = ex.Message;
            }
        }

        private async Task MoveSentenceAsync(int offset)
        {
            try
            {
                if (transcriptSegments.Count == 0)
                {
                    statusLabel.Text = "Transcribe the video first.";
                    return;
                }

                if (mpv == null)
                {
                    statusLabel.Text = "Open a video first.";
                    return;
                }

                if (currentSentenceIndex < 0)
                {
                    currentSentenceIndex = 0;
                }

                currentSentenceIndex = Math.Max(0, Math.Min(transcriptSegments.Count - 1, currentSentenceIndex + offset));
                UpdateCurrentSentenceStatus();

                if (offset < 0)
                {
                    await mpv.PreviousSentenceAsync();
                }
                else
                {
                    await mpv.NextSentenceAsync();
                }
            }
            catch (Exception ex)
            {
                statusLabel.Text = ex.Message;
            }
        }

        private async Task AdjustSentenceBoundaryAsync(bool adjustStart, int direction)
        {
            try
            {
                if (mpv == null || transcriptSegments.Count == 0)
                {
                    statusLabel.Text = "Transcribe the video first.";
                    return;
                }

                await PauseForBoundaryAdjustmentAsync();

                if (currentSentenceIndex < 0)
                {
                    currentSentenceIndex = 0;
                }

                currentSentenceIndex = Math.Max(0, Math.Min(transcriptSegments.Count - 1, currentSentenceIndex));
                var changed = adjustStart
                    ? AdjustSentenceStart(currentSentenceIndex, direction)
                    : AdjustSentenceEnd(currentSentenceIndex, direction);

                if (!changed)
                {
                    statusLabel.Text = "Sentence boundary cannot move further.";
                    return;
                }

                if (!string.IsNullOrWhiteSpace(currentVideoPath))
                {
                    SaveUserEditedMasterTranscript(currentVideoPath, currentTranscriptModel, transcriptSegments);
                    SyncMasterTranscriptTimings(currentVideoPath);
                }

                transcriptBox.Text = BuildTranscriptText("Adjusted transcript timing.", transcriptSegments);
                SelectCurrentSentenceText();

                await LoadSentenceSegmentsIntoPlayerAsync();
                UpdateWaveformSentences();
                if (sentenceModeEnabled)
                {
                    await mpv.PlaySentenceAsync(currentSentenceIndex + 1);
                }

                var boundary = adjustStart
                    ? transcriptSegments[currentSentenceIndex].Start
                    : GetEffectiveSentenceEnd(currentSentenceIndex);
                statusLabel.Text =
                    "Sentence " + (currentSentenceIndex + 1) + " " +
                    (adjustStart ? "start" : "end") + " " +
                    FormatDisplayTime(boundary);
            }
            catch (Exception ex)
            {
                statusLabel.Text = ex.Message;
            }
        }

        private bool AdjustSentenceStart(int sentenceIndex, int direction)
        {
            return SetSentenceStart(sentenceIndex, AddStep(transcriptSegments[sentenceIndex].Start, direction));
        }

        private bool AdjustSentenceEnd(int sentenceIndex, int direction)
        {
            return SetSentenceEnd(sentenceIndex, AddStep(GetEffectiveSentenceEnd(sentenceIndex), direction));
        }

        private bool SetSentenceStart(int sentenceIndex, TimeSpan requested)
        {
            var segment = transcriptSegments[sentenceIndex];
            var minimumGap = TimeSpan.FromMilliseconds(MinimumSentenceDurationMilliseconds);
            var minimum = sentenceIndex > 0
                ? transcriptSegments[sentenceIndex - 1].Start + minimumGap
                : TimeSpan.Zero;
            var maximum = segment.End - minimumGap;
            var adjusted = Clamp(requested, minimum, maximum);

            if (adjusted == segment.Start)
            {
                return false;
            }

            segment.SetStart(adjusted);
            if (sentenceIndex > 0)
            {
                transcriptSegments[sentenceIndex - 1].SetEnd(adjusted);
            }

            return true;
        }

        private bool SetSentenceEnd(int sentenceIndex, TimeSpan requested)
        {
            var segment = transcriptSegments[sentenceIndex];
            var minimumGap = TimeSpan.FromMilliseconds(MinimumSentenceDurationMilliseconds);
            var minimum = segment.Start + minimumGap;
            var maximum = sentenceIndex + 1 < transcriptSegments.Count
                ? transcriptSegments[sentenceIndex + 1].End - minimumGap
                : TimeSpan.MaxValue;
            var adjusted = Clamp(requested, minimum, maximum);

            if (adjusted == GetEffectiveSentenceEnd(sentenceIndex))
            {
                return false;
            }

            segment.SetEnd(adjusted);
            if (sentenceIndex + 1 < transcriptSegments.Count)
            {
                transcriptSegments[sentenceIndex + 1].SetStart(adjusted);
            }

            return true;
        }

        private async Task SetSentenceBoundaryFromWaveformAsync(bool adjustStart, TimeSpan boundary)
        {
            try
            {
                if (mpv == null || transcriptSegments.Count == 0)
                {
                    return;
                }

                await PauseForBoundaryAdjustmentAsync();

                currentSentenceIndex = Math.Max(0, Math.Min(transcriptSegments.Count - 1, currentSentenceIndex));
                var changed = adjustStart
                    ? SetSentenceStart(currentSentenceIndex, boundary)
                    : SetSentenceEnd(currentSentenceIndex, boundary);

                if (!changed)
                {
                    statusLabel.Text = "Sentence boundary cannot move further.";
                    UpdateWaveformSentences();
                    return;
                }

                if (!string.IsNullOrWhiteSpace(currentVideoPath))
                {
                    SaveUserEditedMasterTranscript(currentVideoPath, currentTranscriptModel, transcriptSegments);
                    SyncMasterTranscriptTimings(currentVideoPath);
                }

                transcriptBox.Text = BuildTranscriptText("Adjusted transcript timing.", transcriptSegments);
                SelectCurrentSentenceText();
                await LoadSentenceSegmentsIntoPlayerAsync();
                UpdateWaveformSentences();

                if (sentenceModeEnabled)
                {
                    await mpv.PlaySentenceAsync(currentSentenceIndex + 1);
                }

                statusLabel.Text = "Sentence " + (currentSentenceIndex + 1) + " boundary " + FormatDisplayTime(boundary);
            }
            catch (Exception ex)
            {
                statusLabel.Text = ex.Message;
            }
        }

        private async Task SeekFromWaveformAsync(TimeSpan position)
        {
            if (mpv == null)
            {
                return;
            }

            try
            {
                await mpv.SeekAbsoluteAsync(position.TotalSeconds);
                currentSentenceIndex = FindSentenceIndexAt(position);
                UpdateCurrentSentenceStatus();
                UpdateWaveformSentences();
                waveformPanel.SetPlaybackPosition(position.TotalSeconds, currentSentenceIndex);
            }
            catch (Exception ex)
            {
                statusLabel.Text = ex.Message;
            }
        }

        private async Task PlayCurrentSentenceAsync()
        {
            try
            {
                if (mpv == null || transcriptSegments.Count == 0)
                {
                    statusLabel.Text = "Transcribe the video first.";
                    return;
                }

                if (sentenceModeEnabled)
                {
                    await mpv.RepeatSentenceAsync();
                    return;
                }

                if (currentSentenceIndex < 0)
                {
                    currentSentenceIndex = 0;
                }

                currentSentenceIndex = Math.Max(0, Math.Min(transcriptSegments.Count - 1, currentSentenceIndex));
                await mpv.PlaySentenceAsync(currentSentenceIndex + 1);
                UpdateCurrentSentenceStatus();
            }
            catch (Exception ex)
            {
                statusLabel.Text = ex.Message;
            }
        }

        private async Task StartSentenceModeAtCurrentPositionAsync()
        {
            if (mpv == null || transcriptSegments.Count == 0)
            {
                statusLabel.Text = "Transcribe the video first.";
                return;
            }

            var timePosition = await mpv.GetTimePositionAsync();
            if (timePosition.HasValue)
            {
                currentSentenceIndex = FindSentenceIndexAt(TimeSpan.FromSeconds(timePosition.Value));
                UpdateCurrentSentenceStatus();
            }
            else
            {
                statusLabel.Text = "Sentence mode on.";
            }

            await mpv.EnableSentenceModeAsync();
        }

        private async Task LoadSentenceSegmentsIntoPlayerAsync()
        {
            if (mpv == null)
            {
                return;
            }

            var items = new List<Dictionary<string, object>>();
            for (var i = 0; i < transcriptSegments.Count; i++)
            {
                var segment = transcriptSegments[i];
                items.Add(new Dictionary<string, object>
                {
                    { "start", segment.Start.TotalSeconds },
                    { "end", GetEffectiveSentenceEnd(i).TotalSeconds },
                });
            }

            var serializer = new JavaScriptSerializer();
            await mpv.LoadSentenceSegmentsAsync(serializer.Serialize(items));
        }

        private async Task<TimeSpan?> TryGetPlaybackPositionAsync()
        {
            if (mpv == null)
            {
                return null;
            }

            try
            {
                var timePosition = await mpv.GetTimePositionAsync();
                return timePosition.HasValue ? TimeSpan.FromSeconds(timePosition.Value) : (TimeSpan?)null;
            }
            catch
            {
                return null;
            }
        }

        private int ResolveSentenceIndexForTranscriptReload(TimeSpan? playbackPosition, int previousSentenceIndex)
        {
            if (transcriptSegments.Count == 0)
            {
                return -1;
            }

            if (sentenceModeEnabled && previousSentenceIndex >= 0)
            {
                return Math.Max(0, Math.Min(transcriptSegments.Count - 1, previousSentenceIndex));
            }

            if (playbackPosition.HasValue)
            {
                return FindSentenceIndexAt(playbackPosition.Value);
            }

            if (previousSentenceIndex >= 0)
            {
                return Math.Max(0, Math.Min(transcriptSegments.Count - 1, previousSentenceIndex));
            }

            return 0;
        }

        private void UpdateCurrentSentenceStatus()
        {
            currentSentenceIndex = Math.Max(0, Math.Min(transcriptSegments.Count - 1, currentSentenceIndex));
            SelectCurrentSentenceText();
            UpdateWaveformSentences();
            statusLabel.Text = "Sentence " + (currentSentenceIndex + 1) + " / " + transcriptSegments.Count;
        }

        private async Task UpdateWaveformPlaybackAsync()
        {
            if (!waveformVisible || waveformTimerBusy || mpv == null)
            {
                return;
            }

            waveformTimerBusy = true;
            try
            {
                var timePosition = await mpv.GetTimePositionAsync();
                if (!timePosition.HasValue)
                {
                    return;
                }

                var playbackPosition = TimeSpan.FromSeconds(timePosition.Value);
                if (transcriptSegments.Count > 0)
                {
                    if (sentenceModeEnabled)
                    {
                        currentSentenceIndex = Math.Max(0, Math.Min(transcriptSegments.Count - 1, currentSentenceIndex));
                    }
                    else
                    {
                        var sentenceIndex = FindSentenceIndexAt(playbackPosition);
                        if (sentenceIndex != currentSentenceIndex)
                        {
                            currentSentenceIndex = sentenceIndex;
                            SelectCurrentSentenceText();
                        }
                    }
                }

                UpdateWaveformSentences();
                waveformPanel.SetPlaybackPosition(GetWaveformDisplayPosition(playbackPosition).TotalSeconds, currentSentenceIndex);
            }
            finally
            {
                waveformTimerBusy = false;
            }
        }

        private TimeSpan GetWaveformDisplayPosition(TimeSpan playbackPosition)
        {
            if (!sentenceModeEnabled ||
                currentSentenceIndex < 0 ||
                currentSentenceIndex >= transcriptSegments.Count)
            {
                return playbackPosition;
            }

            return Clamp(
                playbackPosition,
                transcriptSegments[currentSentenceIndex].Start,
                GetEffectiveSentenceEnd(currentSentenceIndex));
        }

        private void UpdateWaveformSentences()
        {
            var items = new List<WaveformSentence>();
            for (var i = 0; i < transcriptSegments.Count; i++)
            {
                items.Add(new WaveformSentence(
                    transcriptSegments[i].Start.TotalSeconds,
                    GetEffectiveSentenceEnd(i).TotalSeconds));
            }

            waveformPanel.SetSentences(items, currentSentenceIndex);
        }

        private int FindSentenceIndexAt(TimeSpan playbackPosition)
        {
            for (var i = 0; i < transcriptSegments.Count; i++)
            {
                var segment = transcriptSegments[i];
                var sentenceEnd = GetEffectiveSentenceEnd(i);

                if (playbackPosition >= segment.Start && playbackPosition < sentenceEnd)
                {
                    return i;
                }

                if (playbackPosition < segment.Start)
                {
                    return i;
                }
            }

            return Math.Max(0, transcriptSegments.Count - 1);
        }

        private TimeSpan GetEffectiveSentenceEnd(int sentenceIndex)
        {
            var segment = transcriptSegments[sentenceIndex];
            var end = segment.End;

            if (sentenceIndex + 1 < transcriptSegments.Count)
            {
                var nextStart = transcriptSegments[sentenceIndex + 1].Start;
                if (nextStart > segment.Start && (end <= segment.Start || nextStart < end))
                {
                    end = nextStart;
                }
            }

            var maxEnd = segment.Start + TimeSpan.FromSeconds(MaxSentencePlaybackSeconds);
            if (end > maxEnd)
            {
                end = maxEnd;
            }

            if (end <= segment.Start)
            {
                end = segment.Start + TimeSpan.FromMilliseconds(100);
            }

            return end;
        }

        private TimeSpan AddStep(TimeSpan value, int direction)
        {
            return value + TimeSpan.FromTicks(GetTimingStep().Ticks * direction);
        }

        private TimeSpan GetTimingStep()
        {
            return TimeSpan.FromSeconds((double)timingStepInput.Value);
        }

        private static TimeSpan Clamp(TimeSpan value, TimeSpan minimum, TimeSpan maximum)
        {
            if (maximum < minimum)
            {
                maximum = minimum;
            }

            if (value < minimum)
            {
                return minimum;
            }

            if (value > maximum)
            {
                return maximum;
            }

            return value;
        }

        private void UpdateSentenceModeButtons()
        {
            var hasSentences = transcriptSegments.Count > 0;
            sentenceModeButton.Enabled = hasSentences;
            waveformButton.Enabled = !string.IsNullOrWhiteSpace(currentVideoPath);
            previousSentenceButton.Enabled = hasSentences && sentenceModeEnabled;
            repeatSentenceButton.Enabled = hasSentences && sentenceModeEnabled;
            nextSentenceButton.Enabled = hasSentences && sentenceModeEnabled;
            sentenceStartEarlierButton.Enabled = hasSentences;
            sentenceStartLaterButton.Enabled = hasSentences;
            sentenceEndEarlierButton.Enabled = hasSentences;
            sentenceEndLaterButton.Enabled = hasSentences;
            timingStepInput.Enabled = hasSentences;
            sentenceModeButton.Text = sentenceModeEnabled ? "Sentence: On" : "Sentence: Off";
        }

        private void SelectCurrentSentenceText()
        {
            transcriptBox.SuspendLayout();
            transcriptBox.SelectAll();
            transcriptBox.SelectionColor = SystemColors.WindowText;
            transcriptBox.SelectionBackColor = SystemColors.Window;
            transcriptBox.SelectionFont = transcriptBox.Font;

            if (currentSentenceIndex < 0 || currentSentenceIndex >= transcriptSegments.Count)
            {
                transcriptBox.Select(0, 0);
                transcriptBox.ResumeLayout();
                return;
            }

            var marker = "[" + (currentSentenceIndex + 1).ToString(CultureInfo.InvariantCulture) + "] ";
            var start = transcriptBox.Text.IndexOf(marker, StringComparison.Ordinal);

            if (start >= 0)
            {
                var nextStart = transcriptBox.Text.IndexOf(Environment.NewLine, start, StringComparison.Ordinal);
                var length = nextStart >= 0 ? nextStart - start : transcriptBox.Text.Length - start;
                transcriptBox.Select(start, length);
                transcriptBox.SelectionColor = CurrentSentenceColor;
                transcriptBox.SelectionBackColor = Color.FromArgb(232, 244, 255);
                transcriptBox.SelectionFont = new Font(transcriptBox.Font, FontStyle.Bold);
                transcriptBox.Select(start, 0);
                transcriptBox.ScrollToCaret();
            }

            transcriptBox.ResumeLayout();
        }

        private double GetSelectedSpeed()
        {
            var selected = speedCombo.SelectedItem as string;
            double speed;
            if (!string.IsNullOrEmpty(selected) &&
                double.TryParse(selected.Replace("x", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out speed) &&
                speed > 0)
            {
                return speed;
            }

            return 1.0;
        }

        private async Task<string> RunTranscriptionAsync(string pythonPath, string scriptPath, string mediaPath, string srtPath, string modelName)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = pythonPath,
                Arguments =
                    QuoteArgument(scriptPath) + " " +
                    QuoteArgument(mediaPath) + " " +
                    "--model " + QuoteArgument(modelName) + " " +
                    "--device cuda " +
                    "--srt " + QuoteArgument(srtPath),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };

            var process = new Process { StartInfo = startInfo };
            activeTranscriptionProcess = process;
            activeTranscriptionOutputPath = srtPath;

            try
            {
                startInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                startInfo.EnvironmentVariables["HF_HOME"] = WhisperModelCacheRoot;
                startInfo.EnvironmentVariables["HUGGINGFACE_HUB_CACHE"] = Path.Combine(WhisperModelCacheRoot, "hub");
                startInfo.EnvironmentVariables["CUDA_PATH"] = @"E:\tools\cuda";
                startInfo.EnvironmentVariables["CUDNN_PATH"] = @"E:\tools\CUDNN Runtime";
                startInfo.EnvironmentVariables["PATH"] = BuildTranscriptionPath(startInfo.EnvironmentVariables["PATH"]);

                if (!process.Start())
                {
                    throw new InvalidOperationException("Could not start the transcription process.");
                }

                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();

                await Task.Run(new Action(process.WaitForExit));

                var output = await outputTask;
                var error = await errorTask;

                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException(error.Trim().Length > 0 ? error.Trim() : "Transcription failed.");
                }

                return output;
            }
            finally
            {
                if (ReferenceEquals(activeTranscriptionProcess, process))
                {
                    activeTranscriptionProcess = null;
                    activeTranscriptionOutputPath = null;
                }

                process.Dispose();
            }
        }

        private static string ExtractTranscriptionDeviceSummary(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                return string.Empty;
            }

            using (var reader = new StringReader(output))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.IndexOf("Using device:", StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        return line.Trim();
                    }
                }
            }

            return string.Empty;
        }

        private async Task PauseForBoundaryAdjustmentAsync()
        {
            if (mpv == null)
            {
                return;
            }

            try
            {
                await mpv.SetPauseAsync(true);
            }
            catch (Exception ex)
            {
                statusLabel.Text = ex.Message;
            }
        }

        private static string ResolveAppPath(string relativePath)
        {
            var basePath = AppDomain.CurrentDomain.BaseDirectory;
            var pathFromBuildOutput = Path.GetFullPath(Path.Combine(basePath, @"..\..", relativePath));

            if (File.Exists(pathFromBuildOutput))
            {
                return pathFromBuildOutput;
            }

            return Path.GetFullPath(Path.Combine(basePath, relativePath));
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static string BuildTranscriptionPath(string existingPath)
        {
            var orderedPaths = new List<string>();
            AddExistingDirectory(orderedPaths, @"E:\tools\cuda\bin");
            AddExistingDirectory(orderedPaths, @"E:\tools\cuda\bin\x64");
            AddExistingDirectory(orderedPaths, @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.4\bin");
            AddExistingDirectory(orderedPaths, @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.3\bin");
            AddExistingDirectory(orderedPaths, @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.2\bin");
            AddExistingDirectory(orderedPaths, @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.1\bin");
            AddExistingDirectory(orderedPaths, @"E:\Application\anaconda\envs\deeplearning\Lib\site-packages\torch\lib");
            AddExistingDirectory(orderedPaths, @"E:\Application\webui\webui_forge_cu121_torch231\system\python\Lib\site-packages\torch\lib");
            AddExistingDirectory(orderedPaths, @"E:\tools\CUDNN Runtime\bin\13.3\x64");

            if (!string.IsNullOrWhiteSpace(existingPath))
            {
                orderedPaths.Add(existingPath);
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var builder = new StringBuilder();
            for (var i = 0; i < orderedPaths.Count; i++)
            {
                var path = orderedPaths[i];
                if (string.IsNullOrWhiteSpace(path) || !seen.Add(path))
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append(';');
                }

                builder.Append(path);
            }

            return builder.ToString();
        }

        private static void AddExistingDirectory(List<string> paths, string candidate)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
            {
                paths.Add(candidate);
            }
        }

        private string GetSelectedWhisperModel()
        {
            var selected = whisperModelCombo.SelectedItem as string;
            return string.IsNullOrWhiteSpace(selected) ? "small" : selected;
        }

        private void UpdateWhisperModelStatus()
        {
            var modelName = GetSelectedWhisperModel();
            var downloaded = IsWhisperModelDownloaded(modelName);
            whisperModelStatusLabel.Text = downloaded ? "Downloaded" : "Not downloaded";
            whisperModelStatusLabel.ForeColor = downloaded ? Color.FromArgb(0, 120, 80) : Color.FromArgb(170, 90, 0);
        }

        private void RefreshTranscriptModelChoices()
        {
            transcriptModelSelectionBusy = true;
            try
            {
                transcriptModelCombo.Items.Clear();

                if (string.IsNullOrWhiteSpace(currentVideoPath))
                {
                    transcriptModelCombo.Enabled = false;
                    UpdateTranscriptModelStatus();
                    return;
                }

                foreach (var modelName in GetKnownWhisperModels())
                {
                    if (File.Exists(GetTranscriptCachePath(currentVideoPath, modelName)))
                    {
                        transcriptModelCombo.Items.Add(modelName);
                    }
                }

                transcriptModelCombo.Enabled = transcriptModelCombo.Items.Count > 0;
                if (!string.IsNullOrWhiteSpace(currentTranscriptModel) &&
                    transcriptModelCombo.Items.Contains(currentTranscriptModel))
                {
                    transcriptModelCombo.SelectedItem = currentTranscriptModel;
                }
                else if (transcriptModelCombo.Items.Count > 0)
                {
                    transcriptModelCombo.SelectedIndex = 0;
                }

                UpdateTranscriptModelStatus();
            }
            finally
            {
                transcriptModelSelectionBusy = false;
            }
        }

        private void UpdateTranscriptModelStatus()
        {
            if (string.IsNullOrWhiteSpace(currentTranscriptModel))
            {
                transcriptModelStatusLabel.Text = "Transcript: none";
                transcriptModelStatusLabel.ForeColor = SystemColors.ControlText;
                return;
            }

            transcriptModelStatusLabel.Text = "Transcript: " + currentTranscriptModel;
            transcriptModelStatusLabel.ForeColor = Color.FromArgb(0, 102, 204);
        }

        private static string[] GetKnownWhisperModels()
        {
            return new[] { "tiny", "base", "small", "medium", "large-v3" };
        }

        private static bool IsWhisperModelDownloaded(string modelName)
        {
            var modelFolder = GetWhisperModelCachePath(modelName);
            var snapshotsFolder = Path.Combine(modelFolder, "snapshots");
            if (!Directory.Exists(snapshotsFolder))
            {
                return false;
            }

            foreach (var snapshot in Directory.GetDirectories(snapshotsFolder))
            {
                if (Directory.GetFiles(snapshot, "*", SearchOption.AllDirectories).Length > 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static string GetWhisperModelCachePath(string modelName)
        {
            return Path.Combine(
                WhisperModelCacheRoot,
                "hub",
                "models--Systran--faster-whisper-" + SanitizeHuggingFaceCacheName(modelName));
        }

        private static string SanitizeHuggingFaceCacheName(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? "small"
                : value.Replace("/", "--").Replace("\\", "--");
        }

        private static string GetTranscriptCachePath(string videoPath, string modelName)
        {
            var directory = Path.GetDirectoryName(videoPath);
            var safeModelName = string.IsNullOrWhiteSpace(modelName) ? "small" : modelName.Replace('/', '-').Replace('\\', '-');
            var fileName = Path.GetFileNameWithoutExtension(videoPath) + ".shadowing." + safeModelName + ".srt";
            return string.IsNullOrEmpty(directory) ? fileName : Path.Combine(directory, fileName);
        }

        private static string GetSharedTimingPath(string videoPath)
        {
            var directory = Path.GetDirectoryName(videoPath);
            var fileName = Path.GetFileNameWithoutExtension(videoPath) + ".shadowing.timings.json";
            return string.IsNullOrEmpty(directory) ? fileName : Path.Combine(directory, fileName);
        }

        private static void ApplyOrPromoteMasterTranscript(string videoPath, string modelName, List<TranscriptSegment> segments)
        {
            if (string.IsNullOrWhiteSpace(videoPath) || segments.Count == 0)
            {
                return;
            }

            TranscriptMasterFile master;
            var hasMaster = TryLoadMasterTranscript(videoPath, out master);
            var modelRank = GetWhisperModelRank(modelName);

            if (!hasMaster || (!IsUserEditedMaster(master) && modelRank > master.ModelRank))
            {
                SaveMasterTranscript(videoPath, CreateMasterTranscript("model", modelName, modelRank, segments));
                SyncMasterTranscriptTimings(videoPath);
                return;
            }

            ApplyTimingsToSegments(segments, ConvertMasterTimings(master));
        }

        private static void SaveUserEditedMasterTranscript(string videoPath, string modelName, List<TranscriptSegment> segments)
        {
            SaveMasterTranscript(videoPath, CreateMasterTranscript(
                "user",
                string.IsNullOrWhiteSpace(modelName) ? "manual" : modelName,
                GetWhisperModelRank(modelName),
                segments));
        }

        private static bool IsUserEditedMaster(TranscriptMasterFile master)
        {
            return master != null && string.Equals(master.Source, "user", StringComparison.OrdinalIgnoreCase);
        }

        private static TranscriptMasterFile CreateMasterTranscript(
            string source,
            string modelName,
            int modelRank,
            List<TranscriptSegment> segments)
        {
            var master = new TranscriptMasterFile
            {
                Version = 2,
                Source = source,
                Model = modelName,
                ModelRank = modelRank,
                Timings = new List<TranscriptTimingFileItem>(),
            };

            for (var i = 0; i < segments.Count; i++)
            {
                master.Timings.Add(new TranscriptTimingFileItem
                {
                    Start = segments[i].Start.TotalSeconds,
                    End = segments[i].End.TotalSeconds,
                });
            }

            return master;
        }

        private static void SaveMasterTranscript(string videoPath, TranscriptMasterFile master)
        {
            var serializer = new JavaScriptSerializer();
            File.WriteAllText(GetSharedTimingPath(videoPath), serializer.Serialize(master), Encoding.UTF8);
        }

        private static void SyncMasterTranscriptTimings(string videoPath)
        {
            TranscriptMasterFile master;
            if (!TryLoadMasterTranscript(videoPath, out master))
            {
                return;
            }

            var timings = ConvertMasterTimings(master);
            foreach (var modelName in GetKnownWhisperModels())
            {
                var transcriptPath = GetTranscriptCachePath(videoPath, modelName);
                if (!File.Exists(transcriptPath))
                {
                    continue;
                }

                var segments = ParseSrtFile(transcriptPath);
                ApplyTimingsToSegments(segments, timings);
                WriteSrtFile(transcriptPath, segments);
            }
        }

        private static bool TryLoadMasterTranscript(string videoPath, out TranscriptMasterFile master)
        {
            master = null;
            var path = GetSharedTimingPath(videoPath);
            if (!File.Exists(path))
            {
                return false;
            }

            try
            {
                var serializer = new JavaScriptSerializer();
                var json = File.ReadAllText(path, Encoding.UTF8);
                if (json.TrimStart().StartsWith("[", StringComparison.Ordinal))
                {
                    var legacyItems = serializer.Deserialize<List<Dictionary<string, object>>>(json);
                    if (legacyItems == null || legacyItems.Count == 0)
                    {
                        return false;
                    }

                    master = new TranscriptMasterFile
                    {
                        Version = 1,
                        Source = "user",
                        Model = "legacy",
                        ModelRank = 0,
                        Timings = new List<TranscriptTimingFileItem>(),
                    };

                    for (var i = 0; i < legacyItems.Count; i++)
                    {
                        object startValue;
                        object endValue;
                        if (!legacyItems[i].TryGetValue("start", out startValue) ||
                            !legacyItems[i].TryGetValue("end", out endValue))
                        {
                            return false;
                        }

                        master.Timings.Add(new TranscriptTimingFileItem
                        {
                            Start = Convert.ToDouble(startValue, CultureInfo.InvariantCulture),
                            End = Convert.ToDouble(endValue, CultureInfo.InvariantCulture),
                        });
                    }

                    return true;
                }

                master = serializer.Deserialize<TranscriptMasterFile>(json);
                return master != null && master.Timings != null && master.Timings.Count > 0;
            }
            catch
            {
                master = null;
                return false;
            }
        }

        private static List<TranscriptTiming> ConvertMasterTimings(TranscriptMasterFile master)
        {
            var items = new List<TranscriptTiming>();
            if (master == null || master.Timings == null)
            {
                return items;
            }

            for (var i = 0; i < master.Timings.Count; i++)
            {
                items.Add(new TranscriptTiming(
                    TimeSpan.FromSeconds(master.Timings[i].Start),
                    TimeSpan.FromSeconds(master.Timings[i].End)));
            }

            return items;
        }

        private static void ApplyTimingsToSegments(List<TranscriptSegment> segments, List<TranscriptTiming> timings)
        {
            if (segments.Count == 0 || timings.Count == 0)
            {
                return;
            }

            if (segments.Count == timings.Count)
            {
                for (var i = 0; i < segments.Count; i++)
                {
                    segments[i].SetStart(timings[i].Start);
                    segments[i].SetEnd(timings[i].End);
                }

                NormalizeAdjacentSegmentText(segments);
                return;
            }

            var aligned = AlignTranscriptTextToTimings(segments, timings);
            segments.Clear();
            segments.AddRange(aligned);
            NormalizeAdjacentSegmentText(segments);
        }

        private static List<TranscriptSegment> AlignTranscriptTextToTimings(
            List<TranscriptSegment> sourceSegments,
            List<TranscriptTiming> timings)
        {
            var aligned = new List<TranscriptSegment>();
            for (var i = 0; i < timings.Count; i++)
            {
                var text = new StringBuilder();
                for (var j = 0; j < sourceSegments.Count; j++)
                {
                    if (Overlaps(sourceSegments[j].Start, sourceSegments[j].End, timings[i].Start, timings[i].End))
                    {
                        if (text.Length > 0)
                        {
                            text.Append(" ");
                        }

                        text.Append(sourceSegments[j].Text);
                    }
                }

                if (text.Length == 0)
                {
                    var fallbackIndex = timings.Count <= 1
                        ? 0
                        : (int)Math.Round(i * (sourceSegments.Count - 1) / (double)(timings.Count - 1));
                    fallbackIndex = Math.Max(0, Math.Min(sourceSegments.Count - 1, fallbackIndex));
                    text.Append(sourceSegments[fallbackIndex].Text);
                }

                aligned.Add(new TranscriptSegment(timings[i].Start, timings[i].End, text.ToString()));
            }

            return aligned;
        }

        private static bool Overlaps(TimeSpan firstStart, TimeSpan firstEnd, TimeSpan secondStart, TimeSpan secondEnd)
        {
            return firstStart < secondEnd && firstEnd > secondStart;
        }

        private static void NormalizeAdjacentSegmentText(List<TranscriptSegment> segments)
        {
            for (var i = 1; i < segments.Count; i++)
            {
                var trimmed = RemoveRepeatedPrefixFromCurrentSegment(segments[i - 1].Text, segments[i].Text);
                if (!string.Equals(trimmed, segments[i].Text, StringComparison.Ordinal))
                {
                    segments[i].SetText(trimmed);
                }
            }
        }

        private static string RemoveRepeatedPrefixFromCurrentSegment(string previousText, string currentText)
        {
            if (string.IsNullOrWhiteSpace(previousText) || string.IsNullOrWhiteSpace(currentText))
            {
                return currentText;
            }

            var previousTokens = GetOverlapTokens(previousText);
            var currentTokens = GetOverlapTokens(currentText);
            if (previousTokens.Count == 0 || currentTokens.Count == 0)
            {
                return currentText;
            }

            const int minimumOverlapWords = 4;
            var maxOverlap = Math.Min(previousTokens.Count, currentTokens.Count - 1);
            for (var overlap = maxOverlap; overlap >= minimumOverlapWords; overlap--)
            {
                var matches = true;
                for (var offset = 0; offset < overlap; offset++)
                {
                    if (!string.Equals(
                        previousTokens[previousTokens.Count - overlap + offset].Normalized,
                        currentTokens[offset].Normalized,
                        StringComparison.Ordinal))
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                {
                    var cutIndex = currentTokens[overlap].StartIndex;
                    return currentText.Substring(cutIndex).TrimStart();
                }
            }

            return currentText;
        }

        private static List<OverlapToken> GetOverlapTokens(string text)
        {
            var tokens = new List<OverlapToken>();
            foreach (Match match in Regex.Matches(text ?? string.Empty, @"[\p{L}\p{N}']+"))
            {
                var normalized = match.Value.Trim().ToLowerInvariant();
                if (normalized.Length == 0)
                {
                    continue;
                }

                tokens.Add(new OverlapToken(normalized, match.Index));
            }

            return tokens;
        }

        private static int GetWhisperModelRank(string modelName)
        {
            switch ((modelName ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "tiny":
                    return 1;
                case "base":
                    return 2;
                case "small":
                    return 3;
                case "medium":
                    return 4;
                case "large-v3":
                    return 5;
                default:
                    return 0;
            }
        }

        private static string GetWaveformCachePath(string videoPath)
        {
            var directory = Path.GetDirectoryName(videoPath);
            var fileName = Path.GetFileNameWithoutExtension(videoPath) + ".shadowing.waveform.txt";
            return string.IsNullOrEmpty(directory) ? fileName : Path.Combine(directory, fileName);
        }

        private static string ResolveFfmpegPath()
        {
            var ffmpegPath = Environment.GetEnvironmentVariable("SHADOWING_FFMPEG_PATH");
            if (!string.IsNullOrWhiteSpace(ffmpegPath))
            {
                return ffmpegPath;
            }

            var bundledPath = @"E:\tools\ffmpeg-2026-06-15-git-44d082edc8-essentials_build\bin\ffmpeg.exe";
            return File.Exists(bundledPath) ? bundledPath : "ffmpeg.exe";
        }

        private static WaveformData GenerateWaveform(string mediaPath)
        {
            const int sampleRate = 8000;
            const int samplesPerPeak = 400;
            var peaks = new List<float>();
            var maxSample = 0;
            var samplesInPeak = 0;
            long totalSamples = 0;

            var startInfo = new ProcessStartInfo
            {
                FileName = ResolveFfmpegPath(),
                Arguments =
                    "-hide_banner -loglevel error " +
                    "-i " + QuoteArgument(mediaPath) + " " +
                    "-vn -ac 1 -ar " + sampleRate.ToString(CultureInfo.InvariantCulture) + " " +
                    "-f s16le pipe:1",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            try
            {
                using (var process = new Process { StartInfo = startInfo })
                {
                    if (!process.Start())
                    {
                        throw new InvalidOperationException("Could not start ffmpeg.");
                    }

                    var errorTask = process.StandardError.ReadToEndAsync();
                    var buffer = new byte[32768];
                    var stream = process.StandardOutput.BaseStream;
                    int bytesRead;
                    while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        for (var offset = 0; offset + 1 < bytesRead; offset += 2)
                        {
                            var sample = (short)(buffer[offset] | (buffer[offset + 1] << 8));
                            var absolute = Math.Abs((int)sample);
                            if (absolute > maxSample)
                            {
                                maxSample = absolute;
                            }

                            samplesInPeak++;
                            totalSamples++;
                            if (samplesInPeak >= samplesPerPeak)
                            {
                                peaks.Add(Math.Min(1f, maxSample / 32768f));
                                maxSample = 0;
                                samplesInPeak = 0;
                            }
                        }
                    }

                    if (samplesInPeak > 0)
                    {
                        peaks.Add(Math.Min(1f, maxSample / 32768f));
                    }

                    process.WaitForExit();
                    var error = errorTask.Result;
                    if (process.ExitCode != 0)
                    {
                        throw new InvalidOperationException(
                            string.IsNullOrWhiteSpace(error) ? "ffmpeg could not generate waveform data." : error.Trim());
                    }
                }
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new InvalidOperationException(
                    "ffmpeg.exe was not found. Install ffmpeg, add it to PATH, or set SHADOWING_FFMPEG_PATH.",
                    ex);
            }

            if (peaks.Count == 0 || totalSamples == 0)
            {
                throw new InvalidOperationException("No audio waveform could be generated for this video.");
            }

            return new WaveformData(peaks.ToArray(), totalSamples / (double)sampleRate);
        }

        private static bool TryLoadWaveformCache(string path, out float[] peaks, out double durationSeconds)
        {
            peaks = new float[0];
            durationSeconds = 0;

            try
            {
                var lines = File.ReadAllLines(path, Encoding.UTF8);
                if (lines.Length < 3 || lines[0] != "SHADOWING_WAVEFORM_V1")
                {
                    return false;
                }

                if (!double.TryParse(lines[1], NumberStyles.Float, CultureInfo.InvariantCulture, out durationSeconds))
                {
                    return false;
                }

                var values = new List<float>();
                for (var i = 2; i < lines.Length; i++)
                {
                    float value;
                    if (float.TryParse(lines[i], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    {
                        values.Add(Math.Max(0, Math.Min(1, value)));
                    }
                }

                peaks = values.ToArray();
                return peaks.Length > 0 && durationSeconds > 0;
            }
            catch
            {
                return false;
            }
        }

        private static void SaveWaveformCache(string path, float[] peaks, double durationSeconds)
        {
            using (var writer = new StreamWriter(path, false, Encoding.UTF8))
            {
                writer.WriteLine("SHADOWING_WAVEFORM_V1");
                writer.WriteLine(durationSeconds.ToString("R", CultureInfo.InvariantCulture));
                for (var i = 0; i < peaks.Length; i++)
                {
                    writer.WriteLine(peaks[i].ToString("R", CultureInfo.InvariantCulture));
                }
            }
        }

        private static string BuildTranscriptText(string header, List<TranscriptSegment> segments)
        {
            if (segments.Count == 0)
            {
                return header;
            }

            var builder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(header))
            {
                var firstLineEnd = header.IndexOf(Environment.NewLine, StringComparison.Ordinal);
                builder.AppendLine(firstLineEnd >= 0 ? header.Substring(0, firstLineEnd) : header);
                builder.AppendLine();
            }

            for (var i = 0; i < segments.Count; i++)
            {
                var segment = segments[i];
                builder.Append("[");
                builder.Append(i + 1);
                builder.Append("] ");
                builder.AppendLine(segment.Text);
            }

            return builder.ToString().TrimEnd();
        }

        private static void WriteSrtFile(string path, List<TranscriptSegment> segments)
        {
            using (var writer = new StreamWriter(path, false, Encoding.UTF8))
            {
                for (var i = 0; i < segments.Count; i++)
                {
                    var segment = segments[i];
                    writer.WriteLine(i + 1);
                    writer.WriteLine(FormatSrtTime(segment.Start) + " --> " + FormatSrtTime(segment.End));
                    writer.WriteLine(segment.Text);
                    writer.WriteLine();
                }
            }
        }

        private static List<TranscriptSegment> ParseSrtFile(string path)
        {
            var segments = new List<TranscriptSegment>();
            var lines = File.ReadAllLines(path, Encoding.UTF8);
            var index = 0;

            while (index < lines.Length)
            {
                while (index < lines.Length && string.IsNullOrWhiteSpace(lines[index]))
                {
                    index++;
                }

                if (index < lines.Length && int.TryParse(lines[index], out _))
                {
                    index++;
                }

                if (index >= lines.Length || !lines[index].Contains("-->"))
                {
                    index++;
                    continue;
                }

                var timestampParts = lines[index].Split(new[] { "-->" }, StringSplitOptions.None);
                index++;

                if (timestampParts.Length != 2 ||
                    !TryParseSrtTime(timestampParts[0].Trim(), out var start) ||
                    !TryParseSrtTime(timestampParts[1].Trim(), out var end))
                {
                    continue;
                }

                var text = new StringBuilder();
                while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index]))
                {
                    if (text.Length > 0)
                    {
                        text.Append(" ");
                    }

                    text.Append(lines[index].Trim());
                    index++;
                }

                if (text.Length > 0)
                {
                    segments.Add(new TranscriptSegment(start, end, text.ToString()));
                }
            }

            return segments;
        }

        private static bool TryParseSrtTime(string value, out TimeSpan time)
        {
            return TimeSpan.TryParseExact(
                value,
                @"hh\:mm\:ss\,fff",
                CultureInfo.InvariantCulture,
                out time);
        }

        private static string FormatDisplayTime(TimeSpan time)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0:00}:{1:00}:{2:00}.{3:000}",
                (int)time.TotalHours,
                time.Minutes,
                time.Seconds,
                time.Milliseconds);
        }

        private static string FormatSrtTime(TimeSpan time)
        {
            return FormatDisplayTime(time).Replace(".", ",");
        }

        private async Task ChangeSpeedAsync()
        {
            try
            {
                var selected = speedCombo.SelectedItem as string;
                if (mpv == null || string.IsNullOrEmpty(selected))
                {
                    return;
                }

                double speed;
                if (double.TryParse(selected.Replace("x", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out speed))
                {
                    await mpv.SetSpeedAsync(speed);
                }
            }
            catch (Exception ex)
            {
                statusLabel.Text = ex.Message;
            }
        }

        private async Task SendAsync(Func<MpvController, Task> command)
        {
            if (mpv == null)
            {
                statusLabel.Text = "Open a video first.";
                return;
            }

            try
            {
                await command(mpv);
            }
            catch (Exception ex)
            {
                statusLabel.Text = ex.Message;
            }
        }

        private async Task EnsurePlayerAsync()
        {
            if (mpv != null && mpv.IsRunning)
            {
                return;
            }

            if (mpv != null)
            {
                await mpv.DisposeAsync();
                mpv = null;
            }

            mpv = await MpvController.StartAsync(videoHost.Handle);
        }

        private async Task RestartPlayerAsync()
        {
            if (mpv != null)
            {
                await mpv.DisposeAsync();
                mpv = null;
            }

            mpv = await MpvController.StartAsync(videoHost.Handle);
        }

        private bool IsTranscriptionRunning()
        {
            return activeTranscriptionProcess != null && !activeTranscriptionProcess.HasExited;
        }

        private async Task CancelActiveTranscriptionAsync()
        {
            var process = activeTranscriptionProcess;
            if (process == null)
            {
                return;
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    await Task.Run(new Action(process.WaitForExit));
                }
            }
            catch
            {
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(activeTranscriptionOutputPath) &&
                    File.Exists(activeTranscriptionOutputPath))
                {
                    File.Delete(activeTranscriptionOutputPath);
                }
            }
            catch
            {
            }
        }

        private async Task ShutdownAsync()
        {
            try
            {
                waveformTimer.Stop();
                await CancelActiveTranscriptionAsync();
                if (mpv != null)
                {
                    await mpv.DisableSentenceModeAsync();
                    await mpv.DisposeAsync();
                    mpv = null;
                }
            }
            catch
            {
                mpv = null;
            }
        }

        private sealed class WaveformData
        {
            public WaveformData(float[] peaks, double durationSeconds)
            {
                Peaks = peaks;
                DurationSeconds = durationSeconds;
            }

            public float[] Peaks { get; private set; }

            public double DurationSeconds { get; private set; }
        }

        private sealed class TranscriptTiming
        {
            public TranscriptTiming(TimeSpan start, TimeSpan end)
            {
                Start = start;
                End = end;
            }

            public TimeSpan Start { get; private set; }

            public TimeSpan End { get; private set; }
        }

        private sealed class TranscriptMasterFile
        {
            public int Version { get; set; }

            public string Source { get; set; }

            public string Model { get; set; }

            public int ModelRank { get; set; }

            public List<TranscriptTimingFileItem> Timings { get; set; }
        }

        private sealed class TranscriptTimingFileItem
        {
            public double Start { get; set; }

            public double End { get; set; }
        }

        private sealed class TranscriptSegment
        {
            public TranscriptSegment(TimeSpan start, TimeSpan end, string text)
            {
                Start = start;
                End = end;
                Text = text;
            }

            public TimeSpan Start { get; private set; }

            public TimeSpan End { get; private set; }

            public string Text { get; private set; }

            public void SetStart(TimeSpan start)
            {
                Start = start;
            }

            public void SetEnd(TimeSpan end)
            {
                End = end;
            }

            public void SetText(string text)
            {
                Text = text;
            }
        }

        private sealed class OverlapToken
        {
            public OverlapToken(string normalized, int startIndex)
            {
                Normalized = normalized;
                StartIndex = startIndex;
            }

            public string Normalized { get; private set; }

            public int StartIndex { get; private set; }
        }
    }
}
