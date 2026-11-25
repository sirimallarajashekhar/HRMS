using System.Text;

namespace Hrms.Persistence;

/// <summary>Maps C# PascalCase names to the snake_case used for tables and columns.</summary>
public static class SnakeCase
{
    public static string From(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var builder = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                var previousIsLowerOrDigit = i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]));
                var startsNewWordAfterAcronym = i > 0 && char.IsUpper(name[i - 1]) && i + 1 < name.Length && char.IsLower(name[i + 1]);
                if (previousIsLowerOrDigit || startsNewWordAfterAcronym)
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
