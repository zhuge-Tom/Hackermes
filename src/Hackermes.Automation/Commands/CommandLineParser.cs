using System.Collections.Generic;
using System.Text;

namespace Hackermes.Automation.Commands;

/// <summary>
/// 命令行分词。支持单双引号与反斜杠转义。
/// <para>
/// 选择器里带空格很常见(<c>div &gt; span.item</c>),文本参数里带引号也很常见,
/// 因此不能简单按空格切分。
/// </para>
/// </summary>
public static class CommandLineParser
{
    public static IReadOnlyList<string> Tokenize(string? input)
    {
        var tokens = new List<string>();

        if (string.IsNullOrWhiteSpace(input))
            return tokens;

        var current = new StringBuilder();
        var inToken = false;
        char? quote = null;

        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];

            if (c == '\\' && i + 1 < input.Length)
            {
                // 反斜杠转义下一个字符,让选择器里的 \" 和路径里的 \ 都能原样传入。
                current.Append(input[++i]);
                inToken = true;
                continue;
            }

            if (quote is { } q)
            {
                if (c == q)
                    quote = null;
                else
                    current.Append(c);

                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                inToken = true;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (inToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    inToken = false;
                }

                continue;
            }

            current.Append(c);
            inToken = true;
        }

        if (inToken)
            tokens.Add(current.ToString());

        return tokens;
    }
}
