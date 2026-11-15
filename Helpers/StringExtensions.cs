namespace KRBWeekly.Helpers;

public static class StringExtensions
{
    public static string Truncate(this string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }

    /// <summary>
    /// 只保留文本末尾（日志框用）：超长时从整行边界裁掉前面的内容，并加一行省略提示。
    /// 单纯在中间截断会切坏行，所以找最近的换行。
    /// </summary>
    public static string TrimToTail(this string? text, int maxChars, string trimNotice = "…（日志过长，已省略前面的内容）")
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (maxChars <= 0 || text.Length <= maxChars) return text;

        var cut = text.Length - maxChars;
        var newline = text.IndexOf('\n', cut);
        var tail = newline >= 0 && newline + 1 < text.Length ? text[(newline + 1)..] : text[cut..];

        return trimNotice.Length == 0 ? tail : trimNotice + "\n" + tail;
    }
}
