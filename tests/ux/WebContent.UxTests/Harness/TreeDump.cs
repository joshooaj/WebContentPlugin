using FlaUI.Core.AutomationElements;
using System.Text;

namespace WebContent.UxTests.Harness;

public static class TreeDump
{
    public static string Of(AutomationElement root, int maxDepth = 10)
    {
        var sb = new StringBuilder();
        Dump(root, 0, maxDepth, sb);
        return sb.ToString();
    }

    private static void Dump(AutomationElement el, int depth, int maxDepth, StringBuilder sb)
    {
        if (el == null || depth > maxDepth) return;
        var indent = new string(' ', depth * 2);
        try
        {
            var value = string.Empty;
            try
            {
                if (el.Patterns.Value.IsSupported)
                {
                    var raw = el.Patterns.Value.Pattern.Value.Value;
                    value = raw != null ? $" value='{raw.Substring(0, Math.Min(80, raw.Length))}'" : string.Empty;
                }
            }
            catch { }
            sb.AppendLine($"{indent}{el.ControlType} name='{el.Name}' aid='{el.AutomationId}' cls='{el.ClassName}' off={el.IsOffscreen}{value}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"{indent}<property error {ex.GetType().Name}>");
        }

        try
        {
            foreach (var child in el.FindAllChildren())
            {
                Dump(child, depth + 1, maxDepth, sb);
            }
        }
        catch { }
    }
}
