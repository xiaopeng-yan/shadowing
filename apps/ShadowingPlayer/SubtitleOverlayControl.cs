using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace ShadowingPlayer
{
    public sealed class SubtitleOverlayControl : Control
    {
        private const TextFormatFlags TextFlags =
            TextFormatFlags.NoPadding |
            TextFormatFlags.NoPrefix;

        private readonly ToolTip clickTooltip = new ToolTip();
        private readonly List<WordHitbox> wordHitboxes = new List<WordHitbox>();
        private string subtitleText = string.Empty;
        private int activeWordIndex = -1;

        public SubtitleOverlayControl()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint,
                true);

            BackColor = Color.FromArgb(18, 18, 18);
            ForeColor = Color.White;
            Font = new Font(FontFamily.GenericSansSerif, 15f, FontStyle.Bold);

            clickTooltip.IsBalloon = true;
            clickTooltip.ShowAlways = true;
            clickTooltip.InitialDelay = 0;
            clickTooltip.ReshowDelay = 0;
            clickTooltip.AutoPopDelay = 1500;
        }

        public void SetSubtitle(string text)
        {
            var normalized = text ?? string.Empty;
            if (string.Equals(subtitleText, normalized, StringComparison.Ordinal))
            {
                return;
            }

            subtitleText = normalized;
            activeWordIndex = -1;
            clickTooltip.Hide(this);
            Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);

            if (string.IsNullOrWhiteSpace(subtitleText))
            {
                return;
            }

            for (var i = 0; i < wordHitboxes.Count; i++)
            {
                if (!wordHitboxes[i].Bounds.Contains(e.Location))
                {
                    continue;
                }

                activeWordIndex = i;
                Invalidate();

                var box = wordHitboxes[i].Bounds;
                clickTooltip.Hide(this);
                clickTooltip.Show(
                    "click detected",
                    this,
                    Math.Max(4, box.Left + (box.Width / 2) - 30),
                    Math.Max(4, box.Top - 28),
                    1400);
                return;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            e.Graphics.Clear(BackColor);
            wordHitboxes.Clear();

            if (string.IsNullOrWhiteSpace(subtitleText))
            {
                return;
            }

            var tokens = Tokenize(subtitleText);
            if (tokens.Count == 0)
            {
                return;
            }

            var lineHeight = TextRenderer.MeasureText(e.Graphics, "Ag", Font, Size.Empty, TextFlags).Height;
            var lineSpacing = 4;
            var paddingX = 18;
            var paddingY = 14;
            var maxLineWidth = Math.Max(120, ClientSize.Width - (paddingX * 2) - 32);

            var lines = new List<List<LayoutToken>>();
            var currentLine = new List<LayoutToken>();
            var currentWidth = 0;

            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];

                if (token.Text == "\n")
                {
                    FinalizeLine(lines, currentLine, ref currentWidth);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(token.Text) && currentLine.Count == 0)
                {
                    continue;
                }

                var tokenSize = TextRenderer.MeasureText(e.Graphics, token.Text, Font, Size.Empty, TextFlags);
                if (!string.IsNullOrWhiteSpace(token.Text) &&
                    currentWidth > 0 &&
                    currentWidth + tokenSize.Width > maxLineWidth)
                {
                    FinalizeLine(lines, currentLine, ref currentWidth);
                }

                if (string.IsNullOrWhiteSpace(token.Text) && currentLine.Count == 0)
                {
                    continue;
                }

                currentLine.Add(new LayoutToken(token.Text, token.IsWord, tokenSize));
                currentWidth += tokenSize.Width;
            }

            FinalizeLine(lines, currentLine, ref currentWidth);

            if (lines.Count == 0)
            {
                return;
            }

            var contentWidth = 0;
            for (var i = 0; i < lines.Count; i++)
            {
                var width = 0;
                for (var j = 0; j < lines[i].Count; j++)
                {
                    width += lines[i][j].Size.Width;
                }

                if (width > contentWidth)
                {
                    contentWidth = width;
                }
            }

            var contentHeight = (lines.Count * lineHeight) + ((lines.Count - 1) * lineSpacing);
            var boxWidth = Math.Min(ClientSize.Width - 12, contentWidth + (paddingX * 2));
            var boxHeight = contentHeight + (paddingY * 2);
            var boxX = Math.Max(6, (ClientSize.Width - boxWidth) / 2);
            var boxY = Math.Max(6, ClientSize.Height - boxHeight - 8);
            var textY = boxY + paddingY;

            using (var backgroundBrush = new SolidBrush(Color.FromArgb(225, 16, 16, 16)))
            using (var borderPen = new Pen(Color.FromArgb(70, 255, 255, 255)))
            using (var activeBrush = new SolidBrush(Color.FromArgb(255, 242, 204)))
            using (var activeTextBrush = new SolidBrush(Color.FromArgb(80, 42, 0)))
            using (var defaultTextBrush = new SolidBrush(ForeColor))
            {
                e.Graphics.FillRectangle(backgroundBrush, boxX, boxY, boxWidth, boxHeight);
                e.Graphics.DrawRectangle(borderPen, boxX, boxY, boxWidth - 1, boxHeight - 1);

                var hitboxIndex = 0;
                for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
                {
                    var line = lines[lineIndex];
                    var lineWidth = 0;
                    for (var tokenIndex = 0; tokenIndex < line.Count; tokenIndex++)
                    {
                        lineWidth += line[tokenIndex].Size.Width;
                    }

                    var textX = boxX + ((boxWidth - lineWidth) / 2);
                    for (var tokenIndex = 0; tokenIndex < line.Count; tokenIndex++)
                    {
                        var token = line[tokenIndex];
                        var tokenRect = new Rectangle(textX, textY, token.Size.Width, lineHeight);

                        if (token.IsWord)
                        {
                            var wordRect = Rectangle.Inflate(tokenRect, 4, 2);
                            wordHitboxes.Add(new WordHitbox(token.Text, wordRect));

                            if (hitboxIndex == activeWordIndex)
                            {
                                e.Graphics.FillRectangle(activeBrush, wordRect);
                                TextRenderer.DrawText(e.Graphics, token.Text, Font, tokenRect, activeTextBrush.Color, TextFlags);
                            }
                            else
                            {
                                TextRenderer.DrawText(e.Graphics, token.Text, Font, tokenRect, defaultTextBrush.Color, TextFlags);
                            }

                            hitboxIndex++;
                        }
                        else
                        {
                            TextRenderer.DrawText(e.Graphics, token.Text, Font, tokenRect, defaultTextBrush.Color, TextFlags);
                        }

                        textX += token.Size.Width;
                    }

                    textY += lineHeight + lineSpacing;
                }
            }
        }

        private static void FinalizeLine(List<List<LayoutToken>> lines, List<LayoutToken> currentLine, ref int currentWidth)
        {
            if (currentLine.Count == 0)
            {
                return;
            }

            while (currentLine.Count > 0 && string.IsNullOrWhiteSpace(currentLine[currentLine.Count - 1].Text))
            {
                currentLine.RemoveAt(currentLine.Count - 1);
            }

            if (currentLine.Count > 0)
            {
                lines.Add(new List<LayoutToken>(currentLine));
            }

            currentLine.Clear();
            currentWidth = 0;
        }

        private static List<Token> Tokenize(string text)
        {
            var tokens = new List<Token>();
            var builder = string.Empty;
            var mode = TokenMode.None;

            for (var i = 0; i < text.Length; i++)
            {
                var value = text[i];
                var nextMode = Classify(value);

                if (nextMode == TokenMode.NewLine)
                {
                    FlushToken(tokens, ref builder, ref mode);
                    tokens.Add(new Token("\n", false));
                    continue;
                }

                if (mode != nextMode && builder.Length > 0)
                {
                    FlushToken(tokens, ref builder, ref mode);
                }

                builder += value;
                mode = nextMode;
            }

            FlushToken(tokens, ref builder, ref mode);
            return tokens;
        }

        private static void FlushToken(List<Token> tokens, ref string builder, ref TokenMode mode)
        {
            if (builder.Length == 0)
            {
                mode = TokenMode.None;
                return;
            }

            tokens.Add(new Token(builder, mode == TokenMode.Word));
            builder = string.Empty;
            mode = TokenMode.None;
        }

        private static TokenMode Classify(char value)
        {
            if (value == '\r' || value == '\n')
            {
                return TokenMode.NewLine;
            }

            if (char.IsWhiteSpace(value))
            {
                return TokenMode.Space;
            }

            if (char.IsLetterOrDigit(value) || value == '\'' || value == '-')
            {
                return TokenMode.Word;
            }

            return TokenMode.Punctuation;
        }

        private enum TokenMode
        {
            None,
            Word,
            Space,
            Punctuation,
            NewLine,
        }

        private sealed class Token
        {
            public Token(string text, bool isWord)
            {
                Text = text;
                IsWord = isWord;
            }

            public string Text { get; private set; }
            public bool IsWord { get; private set; }
        }

        private sealed class LayoutToken
        {
            public LayoutToken(string text, bool isWord, Size size)
            {
                Text = text;
                IsWord = isWord;
                Size = size;
            }

            public string Text { get; private set; }
            public bool IsWord { get; private set; }
            public Size Size { get; private set; }
        }

        private sealed class WordHitbox
        {
            public WordHitbox(string word, Rectangle bounds)
            {
                Word = word;
                Bounds = bounds;
            }

            public string Word { get; private set; }
            public Rectangle Bounds { get; private set; }
        }
    }
}
