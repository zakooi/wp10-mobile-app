using System;
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

    public sealed class RustSelectOption
    {
        public string Value { get; set; }
        public string Label { get; set; }
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
        public bool IsChecked { get; set; }
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

        public List<RustSelectOption> GetSelectOptions()
        {
            var list = new List<RustSelectOption>();
            string raw = GetExtraPart(3);
            if (string.IsNullOrEmpty(raw)) return list;

            foreach (string entry in raw.Split(new[] { '\x1f' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int sep = entry.IndexOf('\x1e');
                if (sep >= 0)
                {
                    string val = entry.Substring(0, sep);
                    string lbl = entry.Substring(sep + 1);
                    list.Add(new RustSelectOption
                    {
                        Value = val,
                        Label = string.IsNullOrEmpty(lbl) ? val : lbl
                    });
                }
                else
                {
                    list.Add(new RustSelectOption { Value = entry, Label = entry });
                }
            }
            return list;
        }

        public string GetSelectDisplayLabel()
        {
            var opts = GetSelectOptions();
            foreach (var opt in opts)
            {
                if (string.Equals(opt.Value, Text, StringComparison.Ordinal))
                {
                    return opt.Label;
                }
            }
            if (!string.IsNullOrEmpty(Text)) return Text;
            string placeholder = GetExtraPart(1);
            return string.IsNullOrEmpty(placeholder) ? "Chọn giá trị" : placeholder;
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
        public string CombinedJs { get; set; }
        public string TransportInfo { get; set; }
        public long ElapsedMs { get; set; }
        public bool IsReaderMode { get; set; }
        public int ExternalCssCount { get; set; }
        public int JsScriptsExecuted { get; set; }
        public int JsErrors { get; set; }
        public List<RenderDisplayItem> Items { get; } = new List<RenderDisplayItem>();
        public List<RenderDisplayItem> HiddenInputs { get; } = new List<RenderDisplayItem>();
    }
}
