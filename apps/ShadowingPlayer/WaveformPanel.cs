using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace ShadowingPlayer
{
    public sealed class WaveformPanel : Panel
    {
        private readonly List<WaveformSentence> sentences = new List<WaveformSentence>();
        private float[] peaks = new float[0];
        private double durationSeconds;
        private double playbackSeconds;
        private int currentSentenceIndex = -1;
        private bool dragging;
        private bool draggingStart;
        private double dragSeconds;
        private string message = "Waveform not loaded.";

        public WaveformPanel()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(22, 26, 32);
            ForeColor = Color.White;
            Height = 130;
        }

        public event EventHandler<WaveformBoundaryChangedEventArgs> BoundaryChanged;

        public event EventHandler BoundaryDragStarted;

        public event EventHandler<WaveformSeekEventArgs> SeekRequested;

        public void SetWaveform(float[] values, double duration)
        {
            peaks = values ?? new float[0];
            durationSeconds = Math.Max(0, duration);
            message = peaks.Length == 0 ? "No waveform data." : string.Empty;
            Invalidate();
        }

        public void SetMessage(string value)
        {
            message = value ?? string.Empty;
            Invalidate();
        }

        public void SetSentences(IEnumerable<WaveformSentence> values, int currentIndex)
        {
            sentences.Clear();
            if (values != null)
            {
                sentences.AddRange(values);
            }

            currentSentenceIndex = currentIndex;
            Invalidate();
        }

        public void SetPlaybackPosition(double seconds, int currentIndex)
        {
            playbackSeconds = Math.Max(0, seconds);
            currentSentenceIndex = currentIndex;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            var graphics = e.Graphics;
            graphics.Clear(BackColor);
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            if (!string.IsNullOrWhiteSpace(message) || peaks.Length == 0 || durationSeconds <= 0)
            {
                DrawCenteredMessage(graphics, string.IsNullOrWhiteSpace(message) ? "No waveform data." : message);
                return;
            }

            var window = GetVisibleWindow();
            DrawCurrentSentenceRegion(graphics, window);
            DrawWaveform(graphics, window);
            DrawBoundary(graphics, window, true);
            DrawBoundary(graphics, window, false);
            DrawPlayhead(graphics, window);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);

            if (e.Button != MouseButtons.Left || !HasCurrentSentence())
            {
                return;
            }

            var window = GetVisibleWindow();
            var startX = TimeToX(GetCurrentSentence().StartSeconds, window);
            var endX = TimeToX(GetCurrentSentence().EndSeconds, window);

            if (Math.Abs(e.X - startX) <= 8)
            {
                dragging = true;
                draggingStart = true;
                dragSeconds = XToTime(e.X, window);
                Capture = true;
                OnBoundaryDragStarted();
            }
            else if (Math.Abs(e.X - endX) <= 8)
            {
                dragging = true;
                draggingStart = false;
                dragSeconds = XToTime(e.X, window);
                Capture = true;
                OnBoundaryDragStarted();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (!HasCurrentSentence())
            {
                Cursor = Cursors.Default;
                return;
            }

            var window = GetVisibleWindow();
            if (dragging)
            {
                dragSeconds = XToTime(e.X, window);
                Invalidate();
                return;
            }

            var startX = TimeToX(GetCurrentSentence().StartSeconds, window);
            var endX = TimeToX(GetCurrentSentence().EndSeconds, window);
            Cursor = Math.Abs(e.X - startX) <= 8 || Math.Abs(e.X - endX) <= 8
                ? Cursors.SizeWE
                : Cursors.Default;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            if (dragging)
            {
                dragging = false;
                Capture = false;
                var handler = BoundaryChanged;
                if (handler != null)
                {
                    handler(this, new WaveformBoundaryChangedEventArgs(draggingStart, dragSeconds));
                }

                Invalidate();
                return;
            }

            if (e.Button == MouseButtons.Left && peaks.Length > 0 && durationSeconds > 0)
            {
                var handler = SeekRequested;
                if (handler != null)
                {
                    handler(this, new WaveformSeekEventArgs(XToTime(e.X, GetVisibleWindow())));
                }
            }
        }

        private void DrawCenteredMessage(Graphics graphics, string text)
        {
            using (var brush = new SolidBrush(Color.FromArgb(210, 220, 230)))
            {
                var size = graphics.MeasureString(text, Font);
                graphics.DrawString(text, Font, brush, (Width - size.Width) / 2f, (Height - size.Height) / 2f);
            }
        }

        private void OnBoundaryDragStarted()
        {
            var handler = BoundaryDragStarted;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void DrawWaveform(Graphics graphics, TimeWindow window)
        {
            var centerY = Height / 2f;
            var usableHeight = Math.Max(8, Height - 28);

            using (var pen = new Pen(Color.FromArgb(98, 186, 255)))
            {
                for (var x = 0; x < Width; x++)
                {
                    var startTime = XToTime(x, window);
                    var endTime = XToTime(x + 1, window);
                    var startIndex = TimeToPeakIndex(startTime);
                    var endIndex = Math.Max(startIndex, TimeToPeakIndex(endTime));
                    var amplitude = GetPeakAmplitude(startIndex, endIndex);
                    var halfHeight = amplitude * usableHeight / 2f;
                    graphics.DrawLine(pen, x, centerY - halfHeight, x, centerY + halfHeight);
                }
            }
        }

        private void DrawCurrentSentenceRegion(Graphics graphics, TimeWindow window)
        {
            if (!HasCurrentSentence())
            {
                return;
            }

            var sentence = GetCurrentSentence();
            var x1 = TimeToX(sentence.StartSeconds, window);
            var x2 = TimeToX(sentence.EndSeconds, window);
            using (var brush = new SolidBrush(Color.FromArgb(45, 0, 102, 204)))
            {
                graphics.FillRectangle(brush, Math.Min(x1, x2), 0, Math.Abs(x2 - x1), Height);
            }
        }

        private void DrawBoundary(Graphics graphics, TimeWindow window, bool start)
        {
            if (!HasCurrentSentence())
            {
                return;
            }

            var sentence = GetCurrentSentence();
            var seconds = dragging && draggingStart == start
                ? dragSeconds
                : start ? sentence.StartSeconds : sentence.EndSeconds;
            var x = TimeToX(seconds, window);
            using (var pen = new Pen(start ? Color.FromArgb(255, 210, 89) : Color.FromArgb(83, 232, 139), 2f))
            {
                graphics.DrawLine(pen, x, 0, x, Height);
            }
        }

        private void DrawPlayhead(Graphics graphics, TimeWindow window)
        {
            if (playbackSeconds < window.StartSeconds || playbackSeconds > window.EndSeconds)
            {
                return;
            }

            var x = TimeToX(playbackSeconds, window);
            using (var pen = new Pen(Color.FromArgb(255, 92, 92), 2f))
            {
                graphics.DrawLine(pen, x, 0, x, Height);
            }
        }

        private bool HasCurrentSentence()
        {
            return currentSentenceIndex >= 0 && currentSentenceIndex < sentences.Count;
        }

        private WaveformSentence GetCurrentSentence()
        {
            return sentences[currentSentenceIndex];
        }

        private TimeWindow GetVisibleWindow()
        {
            if (!HasCurrentSentence())
            {
                return new TimeWindow(0, Math.Max(1, durationSeconds));
            }

            var sentence = GetCurrentSentence();
            var sentenceDuration = Math.Max(0.25, sentence.EndSeconds - sentence.StartSeconds);
            var context = Math.Max(0.6, Math.Min(2.0, sentenceDuration * 0.75));
            var start = Math.Max(0, sentence.StartSeconds - context);
            var end = Math.Min(durationSeconds, sentence.EndSeconds + context);

            if (end - start < 1.5)
            {
                var middle = (start + end) / 2.0;
                start = Math.Max(0, middle - 0.75);
                end = Math.Min(durationSeconds, middle + 0.75);
            }

            if (end <= start)
            {
                end = start + 1;
            }

            return new TimeWindow(start, end);
        }

        private int TimeToPeakIndex(double seconds)
        {
            if (durationSeconds <= 0 || peaks.Length == 0)
            {
                return 0;
            }

            var index = (int)Math.Floor(seconds / durationSeconds * peaks.Length);
            return Math.Max(0, Math.Min(peaks.Length - 1, index));
        }

        private float GetPeakAmplitude(int startIndex, int endIndex)
        {
            var max = 0f;
            startIndex = Math.Max(0, Math.Min(peaks.Length - 1, startIndex));
            endIndex = Math.Max(0, Math.Min(peaks.Length - 1, endIndex));

            for (var i = startIndex; i <= endIndex; i++)
            {
                if (peaks[i] > max)
                {
                    max = peaks[i];
                }
            }

            return max;
        }

        private int TimeToX(double seconds, TimeWindow window)
        {
            var ratio = (seconds - window.StartSeconds) / Math.Max(0.001, window.EndSeconds - window.StartSeconds);
            return (int)Math.Round(ratio * Width);
        }

        private double XToTime(int x, TimeWindow window)
        {
            var ratio = Width <= 0 ? 0 : x / (double)Width;
            ratio = Math.Max(0, Math.Min(1, ratio));
            return window.StartSeconds + ratio * (window.EndSeconds - window.StartSeconds);
        }

        private struct TimeWindow
        {
            public TimeWindow(double startSeconds, double endSeconds)
            {
                StartSeconds = startSeconds;
                EndSeconds = endSeconds;
            }

            public double StartSeconds { get; private set; }

            public double EndSeconds { get; private set; }
        }
    }

    public sealed class WaveformSentence
    {
        public WaveformSentence(double startSeconds, double endSeconds)
        {
            StartSeconds = startSeconds;
            EndSeconds = endSeconds;
        }

        public double StartSeconds { get; private set; }

        public double EndSeconds { get; private set; }
    }

    public sealed class WaveformBoundaryChangedEventArgs : EventArgs
    {
        public WaveformBoundaryChangedEventArgs(bool isStartBoundary, double seconds)
        {
            IsStartBoundary = isStartBoundary;
            Seconds = seconds;
        }

        public bool IsStartBoundary { get; private set; }

        public double Seconds { get; private set; }
    }

    public sealed class WaveformSeekEventArgs : EventArgs
    {
        public WaveformSeekEventArgs(double seconds)
        {
            Seconds = seconds;
        }

        public double Seconds { get; private set; }
    }
}
