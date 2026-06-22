using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
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
        private readonly Button sentenceStartEarlierButton = new Button();
        private readonly Button sentenceStartLaterButton = new Button();
        private readonly Button sentenceEndEarlierButton = new Button();
        private readonly Button sentenceEndLaterButton = new Button();
        private readonly ComboBox speedCombo = new ComboBox();
        private readonly ComboBox whisperModelCombo = new ComboBox();
        private readonly NumericUpDown timingStepInput = new NumericUpDown();
        private readonly Label statusLabel = new Label();
        private readonly RichTextBox transcriptBox = new RichTextBox();
        private readonly List<TranscriptSegment> transcriptSegments = new List<TranscriptSegment>();
        private MpvController mpv;
        private string currentVideoPath;
        private string currentTranscriptPath;
        private bool subtitlesVisible = true;
        private bool sentenceModeEnabled;
        private int currentSentenceIndex = -1;

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
            toolbar.Controls.Add(subtitlesButton);
            toolbar.Controls.Add(sentenceModeButton);
            toolbar.Controls.Add(previousSentenceButton);
            toolbar.Controls.Add(repeatSentenceButton);
            toolbar.Controls.Add(nextSentenceButton);
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

            Controls.Add(videoHost);
            Controls.Add(transcriptBox);
            Controls.Add(toolbar);
            FormClosing += async delegate { await ShutdownAsync(); };
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
                    transcriptSegments.Clear();
                    currentSentenceIndex = -1;
                    subtitlesVisible = true;
                    subtitlesButton.Text = "Subtitles: On";
                    UpdateSentenceModeButtons();
                    transcriptBox.Text = "Transcript will appear here.";
                    statusLabel.Text = Path.GetFileName(dialog.FileName);

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
                return;
            }

            await Task.Delay(500);
            await LoadTranscriptFromSrtAsync(srtPath, "Loaded cached transcript.");
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
                    await LoadTranscriptFromSrtAsync(srtPath, "Loaded cached transcript.");
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

                await RunTranscriptionAsync(pythonPath, scriptPath, currentVideoPath, srtPath, modelName);
                await LoadTranscriptFromSrtAsync(srtPath, "Transcript saved and loaded as subtitles.");
            }
            catch (Exception ex)
            {
                transcriptBox.Text = ex.Message;
                statusLabel.Text = "Transcription failed.";
            }
            finally
            {
                transcribeButton.Enabled = true;
            }
        }

        private async Task LoadTranscriptFromSrtAsync(string srtPath, string message)
        {
            transcriptSegments.Clear();
            transcriptSegments.AddRange(ParseSrtFile(srtPath));
            currentTranscriptPath = srtPath;
            currentSentenceIndex = transcriptSegments.Count > 0 ? 0 : -1;
            transcriptBox.Text = BuildTranscriptText(message, transcriptSegments);

            await mpv.AddSubtitleAsync(srtPath);
            await Task.Delay(100);
            subtitlesVisible = true;
            subtitlesButton.Text = "Subtitles: On";
            await mpv.SetSubtitleVisibilityAsync(true);
            await LoadSentenceSegmentsIntoPlayerAsync();
            UpdateSentenceModeButtons();
            SelectCurrentSentenceText();
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

                if (!string.IsNullOrWhiteSpace(currentTranscriptPath))
                {
                    WriteSrtFile(currentTranscriptPath, transcriptSegments);
                }

                transcriptBox.Text = BuildTranscriptText("Adjusted transcript timing.", transcriptSegments);
                SelectCurrentSentenceText();

                await LoadSentenceSegmentsIntoPlayerAsync();
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
            var segment = transcriptSegments[sentenceIndex];
            var minimumGap = TimeSpan.FromMilliseconds(MinimumSentenceDurationMilliseconds);
            var proposed = AddStep(segment.Start, direction);
            var minimum = sentenceIndex > 0
                ? transcriptSegments[sentenceIndex - 1].Start + minimumGap
                : TimeSpan.Zero;
            var maximum = segment.End - minimumGap;
            var adjusted = Clamp(proposed, minimum, maximum);

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

        private bool AdjustSentenceEnd(int sentenceIndex, int direction)
        {
            var segment = transcriptSegments[sentenceIndex];
            var minimumGap = TimeSpan.FromMilliseconds(MinimumSentenceDurationMilliseconds);
            var proposed = AddStep(GetEffectiveSentenceEnd(sentenceIndex), direction);
            var minimum = segment.Start + minimumGap;
            var maximum = sentenceIndex + 1 < transcriptSegments.Count
                ? transcriptSegments[sentenceIndex + 1].End - minimumGap
                : TimeSpan.MaxValue;
            var adjusted = Clamp(proposed, minimum, maximum);

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

        private void UpdateCurrentSentenceStatus()
        {
            currentSentenceIndex = Math.Max(0, Math.Min(transcriptSegments.Count - 1, currentSentenceIndex));
            SelectCurrentSentenceText();
            statusLabel.Text = "Sentence " + (currentSentenceIndex + 1) + " / " + transcriptSegments.Count;
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

        private static async Task<string> RunTranscriptionAsync(string pythonPath, string scriptPath, string mediaPath, string srtPath, string modelName)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = pythonPath,
                Arguments =
                    QuoteArgument(scriptPath) + " " +
                    QuoteArgument(mediaPath) + " " +
                    "--model " + QuoteArgument(modelName) + " " +
                    "--srt " + QuoteArgument(srtPath),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };

            using (var process = new Process { StartInfo = startInfo })
            {
                startInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                startInfo.EnvironmentVariables["HF_HOME"] = WhisperModelCacheRoot;
                startInfo.EnvironmentVariables["HUGGINGFACE_HUB_CACHE"] = Path.Combine(WhisperModelCacheRoot, "hub");

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

        private string GetSelectedWhisperModel()
        {
            var selected = whisperModelCombo.SelectedItem as string;
            return string.IsNullOrWhiteSpace(selected) ? "small" : selected;
        }

        private static string GetTranscriptCachePath(string videoPath, string modelName)
        {
            var directory = Path.GetDirectoryName(videoPath);
            var safeModelName = string.IsNullOrWhiteSpace(modelName) ? "small" : modelName.Replace('/', '-').Replace('\\', '-');
            var fileName = Path.GetFileNameWithoutExtension(videoPath) + ".shadowing." + safeModelName + ".srt";
            return string.IsNullOrEmpty(directory) ? fileName : Path.Combine(directory, fileName);
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

        private async Task ShutdownAsync()
        {
            try
            {
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
        }
    }
}
