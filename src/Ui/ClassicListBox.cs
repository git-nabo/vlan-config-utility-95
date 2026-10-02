using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using VlanConfig95.Core;

namespace VlanConfig95.Ui
{
    /// <summary>One row shown in the adapter list.</summary>
    public sealed class AdapterListItem
    {
        public AdapterInfo Adapter;
        public string PrimaryText;
        public string SecondaryText;

        public AdapterListItem(AdapterInfo adapter, string primary, string secondary)
        {
            Adapter = adapter;
            PrimaryText = primary;
            SecondaryText = secondary;
        }
    }

    /// <summary>
    /// Owner-drawn Windows 95 list view: white sunken field, navy selection bar,
    /// 16px-style row markers and a classic 3D scrollbar. Selection is single-select.
    /// </summary>
    public class ClassicListBox : Control
    {
        private readonly List<AdapterListItem> _items = new List<AdapterListItem>();
        private int _selectedIndex = -1;
        private int _scrollIndex;
        private int _hoverIndex = -1;
        private bool _hasFocus;

        private const int DefaultRowHeight = 30;
        private const int ScrollBarWidth = 16;

        public event EventHandler SelectedIndexChanged;

        public ClassicListBox()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, true);
            Font = Win95Style.CreateFont();
            BackColor = Win95Style.FieldBack;
            TabStop = true;
        }

        public IList<AdapterListItem> Items
        {
            get { return _items; }
        }

        public int RowHeight
        {
            get { return RowHeightValue; }
            set
            {
                RowHeightValue = Math.Max(18, value);
                Invalidate();
            }
        }

        private int RowHeightValue = DefaultRowHeight;

        public int SelectedIndex
        {
            get { return _selectedIndex; }
            set
            {
                if (value < -1 || value >= _items.Count)
                    value = -1;

                if (_selectedIndex == value)
                    return;

                _selectedIndex = value;
                EnsureVisible();
                Invalidate();

                EventHandler h = SelectedIndexChanged;
                if (h != null)
                    h(this, EventArgs.Empty);
            }
        }

        public AdapterListItem SelectedItem
        {
            get
            {
                if (_selectedIndex < 0 || _selectedIndex >= _items.Count)
                    return null;
                return _items[_selectedIndex];
            }
        }

        /// <summary>Number of rows that fit in the client area.</summary>
        private int VisibleRowCount
        {
            get { return Math.Max(1, (Height - 4) / RowHeightValue); }
        }

        public bool NeedsScrollBar
        {
            get { return _items.Count > VisibleRowCount; }
        }
/// <summary>Replaces all items, resetting the selection.</summary>
        public void SetItems(IEnumerable<AdapterListItem> items)
        {
            _items.Clear();
            if (items != null)
                _items.AddRange(new List<AdapterListItem>(items).ToArray());

            _selectedIndex = -1;
            _scrollIndex = 0;
            _hoverIndex = -1;
            Invalidate();
        }

        public void Clear()
        {
            _items.Clear();
            _selectedIndex = -1;
            _scrollIndex = 0;
            Invalidate();
        }

        private void EnsureVisible()
        {
            if (_selectedIndex < 0)
                return;

            if (_selectedIndex < _scrollIndex)
                _scrollIndex = _selectedIndex;
            else if (_selectedIndex >= _scrollIndex + VisibleRowCount)
                _scrollIndex = _selectedIndex - VisibleRowCount + 1;
        }

        private int IndexFromPoint(int y)
        {
            int index = _scrollIndex + ((y - 2) / RowHeightValue);
            if (index < 0 || index >= _items.Count)
                return -1;
            return index;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            int index = IndexFromPoint(e.Y);
            if (index != _hoverIndex)
            {
                _hoverIndex = index;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverIndex != -1)
            {
                _hoverIndex = -1;
                Invalidate();
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            ScrollBy(-Math.Sign(e.Delta) * 3);
        }

        public void ScrollBy(int delta)
        {
            int maxScroll = Math.Max(0, _items.Count - VisibleRowCount);
            int newScroll = _scrollIndex + delta;

            if (newScroll < 0) newScroll = 0;
            if (newScroll > maxScroll) newScroll = maxScroll;

            if (newScroll != _scrollIndex)
            {
                _scrollIndex = newScroll;
                Invalidate();
            }
        }

        public void ScrollTo(int index)
        {
            _scrollIndex = index;
            Invalidate();
        }

        public int ScrollPosition
        {
            get { return _scrollIndex; }
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            _hasFocus = true;
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            _hasFocus = false;
            Invalidate();
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Up:
                case Keys.Down:
                case Keys.PageUp:
                case Keys.PageDown:
                case Keys.Home:
                case Keys.End:
                    return true;
            }
            return base.IsInputKey(keyData);
        }

protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Win95Style.PrepareGraphics(g);

            int sbWidth = NeedsScrollBar ? ScrollBarWidth : 0;
            int listRight = Width - sbWidth - 2;

            // White sunken field behind the rows.
            Win95Style.FillRect(g, Win95Style.FieldBack, 0, 0, Width, Height);
            Win95Style.DrawSunkenBorder(g, new Rectangle(0, 0, Width, Height), 2);

            System.Drawing.Region saved = g.Clip;
            g.SetClip(new Rectangle(2, 2, listRight - 2, Height - 4));

            int visible = VisibleRowCount;

            for (int i = 0; i < visible; i++)
            {
                int index = _scrollIndex + i;
                if (index >= _items.Count)
                    break;

                AdapterListItem item = _items[index];
                int y = 2 + (i * RowHeightValue);
                Rectangle row = new Rectangle(2, y, listRight - 2, RowHeightValue);

                bool isSelected = (index == _selectedIndex);

                if (isSelected)
                {
                    Win95Style.FillRect(g, _hasFocus ? Win95Style.SelectionBack
                                                     : Win95Style.SelectionInactiveBack,
                                        row.Left, row.Top, row.Width, row.Height);
                }
                else if (index == _hoverIndex)
                {
                    Win95Style.FillRect(g, Win95Style.FaceLight, row.Left, row.Top, row.Width, row.Height);
                }

                Color textColor = isSelected
                    ? (_hasFocus ? Win95Style.SelectionText : Win95Style.WindowText)
                    : Win95Style.FieldText;

                DrawStatusMarker(g, item, row);

                TextRenderer.DrawText(g, item.PrimaryText, Win95Style.BoldFont,
                    new Rectangle(row.Left + 22, row.Top + 2, row.Width - 26, 14),
                    textColor, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

                TextRenderer.DrawText(g, item.SecondaryText, Font,
                    new Rectangle(row.Left + 22, row.Top + 16, row.Width - 26, 13),
                    textColor, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

                Win95Style.DrawLine(g, Win95Style.FaceLight, row.Left, row.Bottom - 1,
                                    row.Right - 1, row.Bottom - 1);
            }

            g.Clip = saved;

            if (NeedsScrollBar)
                DrawScrollBar(g, new Rectangle(Width - ScrollBarWidth - 1, 1, ScrollBarWidth, Height - 2));

            if (_hasFocus)
                Win95Style.DrawFocusRect(g, new Rectangle(1, 1, Width - 2, Height - 2));
        }

        /// <summary>
        /// Draws the leading status glyph: a green square for a connected adapter and a
        /// hollow grey square when it is disconnected.
        /// </summary>
        private void DrawStatusMarker(Graphics g, AdapterListItem item, Rectangle row)
        {
            int cy = row.Top + (RowHeightValue / 2);

            if (item.Adapter != null && item.Adapter.IsConnected)
            {
                Win95Style.FillRect(g, Color.FromArgb(0x00, 0x80, 0x00), 7, cy - 4, 8, 8);
                Win95Style.DrawEdgePairPublic(g, new Rectangle(7, cy - 4, 8, 8),
                                              Color.FromArgb(0xC0, 0xE0, 0xC0),
                                              Color.FromArgb(0x00, 0x40, 0x00));
            }
            else
            {
                Win95Style.FillRect(g, Win95Style.Face, 7, cy - 4, 8, 8);
                Win95Style.DrawEdgePairPublic(g, new Rectangle(7, cy - 4, 8, 8),
                                              Win95Style.FaceDark, Win95Style.FaceLight);
            }
        }
/// <summary>Draws the classic 3D vertical scrollbar with arrow buttons and a dithered thumb.</summary>
        private void DrawScrollBar(Graphics g, Rectangle r)
        {
            Win95Style.FillRect(g, Win95Style.Face, r.X, r.Y, r.Width, r.Height);
            Win95Style.DrawEdgePairPublic(g, r, Win95Style.FaceLight, Win95Style.FaceShadow);

            int arrowSize = ScrollBarWidth;

            Rectangle upButton = new Rectangle(r.X + 1, r.Y + 1, r.Width - 2, arrowSize);
            DrawArrowButton(g, upButton, _scrollIndex > 0, true);

            Rectangle downButton = new Rectangle(r.X + 1, r.Bottom - arrowSize - 1, r.Width - 2, arrowSize);
            DrawArrowButton(g, downButton, _scrollIndex < _items.Count - VisibleRowCount, false);

            int trackTop = upButton.Bottom;
            int trackHeight = downButton.Top - trackTop;
            if (trackHeight <= 0)
                return;

            // Windows 95 used a 50% dither pattern for the empty track.
            Win95Style.FillDither(g, new Rectangle(r.X + 1, trackTop, r.Width - 2, trackHeight),
                                  Win95Style.FaceLight, Win95Style.Face);

            int pageSize = VisibleRowCount;
            int thumbHeight = Math.Max(8, (int)((double)trackHeight * pageSize / Math.Max(pageSize, _items.Count)));

            int maxScroll = Math.Max(1, _items.Count - pageSize);
            double fraction = (double)_scrollIndex / maxScroll;
            if (fraction > 1.0) fraction = 1.0;

            int thumbY = trackTop + (int)(fraction * (trackHeight - thumbHeight));
            Rectangle thumb = new Rectangle(r.X + 1, thumbY, r.Width - 2, thumbHeight);

            Win95Style.FillRect(g, Win95Style.Face, thumb.X, thumb.Y, thumb.Width, thumb.Height);
            Win95Style.DrawRaisedBorder(g, thumb, 2);

            // Thumb grip: two highlight/shadow line pairs, like the original.
            int gripY = thumb.Y + (thumb.Height / 2) - 3;
            for (int i = 0; i < 3; i++)
            {
                Win95Style.DrawLine(g, Win95Style.FaceShadow, thumb.X + 4, gripY + (i * 3),
                                    thumb.Right - 5, gripY + (i * 3));
                Win95Style.DrawLine(g, Color.White, thumb.X + 4, gripY + (i * 3) + 1,
                                    thumb.Right - 5, gripY + (i * 3) + 1);
            }
        }

        private static void DrawArrowButton(Graphics g, Rectangle r, bool enabled, bool up)
        {
            Win95Style.FillRect(g, Win95Style.Face, r.X, r.Y, r.Width, r.Height);
            Win95Style.DrawRaisedBorder(g, r, 2);

            Color c = enabled ? Win95Style.WindowText : Win95Style.ButtonTextDisabled;
            int cx = r.X + (r.Width / 2);
            int cy = r.Y + (r.Height / 2);

            // Filled triangles built from scanlines for the chunky 95 look.
            for (int i = 0; i < 4; i++)
            {
                int halfWidth = 4 - i;
                int y = up ? cy + 2 - i : cy - 3 + i;
                Win95Style.DrawLine(g, c, cx - halfWidth, y, cx + halfWidth, y);
            }
        }

        /// <summary>Maps a click inside the scrollbar to a scroll action.</summary>
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (!NeedsScrollBar)
            {
                base.OnMouseDown(e);
                return;
            }

            Rectangle sb = new Rectangle(Width - ScrollBarWidth - 1, 1, ScrollBarWidth, Height - 2);
            if (!sb.Contains(e.Location))
            {
                base.OnMouseDown(e);
                Focus();

                int index = IndexFromPoint(e.Y);
                if (index >= 0)
                    SelectedIndex = index;

                return;
            }

            base.OnMouseDown(e);
            Focus();

            if (e.Y <= sb.Y + ScrollBarWidth)
            {
                ScrollBy(-1);
                return;
            }

            if (e.Y >= sb.Bottom - ScrollBarWidth)
            {
                ScrollBy(1);
                return;
            }

            // Clicking the track pages up or down.
            int trackTop = sb.Y + ScrollBarWidth + 1;
            int trackHeight = sb.Height - (ScrollBarWidth * 2) - 2;
            if (trackHeight <= 0)
                return;

            int page = Math.Max(1, VisibleRowCount - 1);
            int thumbHeight = Math.Max(8, (int)((double)trackHeight * VisibleRowCount /
                                               Math.Max(VisibleRowCount, _items.Count)));

            int maxScroll = Math.Max(1, _items.Count - VisibleRowCount);
            double fraction = (double)_scrollIndex / maxScroll;
            if (fraction > 1.0) fraction = 1.0;

            int thumbY = trackTop + (int)(fraction * (trackHeight - thumbHeight));

            if (e.Y < thumbY)
                ScrollBy(-page);
            else
                ScrollBy(page);
        }
    }
}
