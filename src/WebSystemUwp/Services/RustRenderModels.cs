using System.Collections.Generic;
using Windows.UI;

namespace WebSystemUwp.Services
{
    public enum RenderItemKind : byte
    {
        Box = 0,
        Text = 1,
        Link = 2,
        Divider = 3,
        Image = 4,
        Input = 5,
        Button = 6,
        Hidden = 7
    }

    public sealed class RenderDisplayItem
    {
        public RenderItemKind Kind { get; set; }
        public bool Bold { get; set; }
        public bool Italic { get; set; }
        public bool Underline { get; set; }
        public bool IsPassword { get; set; }
        public bool IsMultiline { get; set; }
        public bool CenterText { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Width { get; set; }
        public float Height { get; set; }
        public float FontSize { get; set; }
        public Color ForegroundColor { get; set; }
        public Color BackgroundColor { get; set; }
        public Color BorderColor { get; set; }
        public int FormId { get; set; } = -1;
        public string Text { get; set; }
        public string Href { get; set; }
        public string Extra { get; set; }

        public string GetExtraPart(int index)
        {
            if (string.IsNullOrEmpty(Extra)) return string.Empty;
            string[] parts = Extra.Split('\t');
            return index >= 0 && index < parts.Length ? parts[index] : string.Empty;
        }
    }

    public sealed class RustRenderPage
    {
        public string Url { get; set; }
        public string Title { get; set; }
        public float ContentWidth { get; set; }
        public float ContentHeight { get; set; }
        public string SourceHtml { get; set; }
        public string CombinedCss { get; set; }
        public string TransportInfo { get; set; }
        public long ElapsedMs { get; set; }
        public bool IsReaderMode { get; set; }
        public int ExternalCssCount { get; set; }
        public List<RenderDisplayItem> Items { get; } = new List<RenderDisplayItem>();
        public List<RenderDisplayItem> HiddenInputs { get; } = new List<RenderDisplayItem>();
    }
}
