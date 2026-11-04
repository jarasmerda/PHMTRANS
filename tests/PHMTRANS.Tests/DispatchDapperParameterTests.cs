using System.Reflection;
using System.Text.RegularExpressions;
using PHMTRANS.Dispatch;
using Xunit;

namespace PHMTRANS.Tests;

public sealed class DispatchDapperParameterTests
{
    [Fact]
    public void Sql_parameters_match_the_object_passed_to_dapper()
    {
        var root = FindRepoRoot();
        var dispatch = Path.Combine(root, "Dispatch");
        var bindings = Directory.GetFiles(dispatch, "*.cs")
            .SelectMany(path => DapperCallScanner.Scan(File.ReadAllText(path), Path.GetFileName(path)))
            .ToArray();

        Assert.True(
            bindings.Length >= 28,
            $"Scanner našel jen {bindings.Length} Dapper volání. Očekává se každé Command/CommandDefinition v Dispatch/.");

        var parameterized = bindings.Where(binding => binding.Properties is not null).ToArray();
        Assert.True(
            parameterized.Length >= 16,
            $"Scanner našel jen {parameterized.Length} dotazů s parametrovým objektem.");

        var upsert = Assert.Single(bindings, binding =>
            binding.File == "SqlOrderStore.cs" &&
            binding.Sql.Contains("SELECT TOP (1) Id", StringComparison.Ordinal) &&
            binding.Sql.Contains("FROM dbo.Disp_SyncQueue", StringComparison.Ordinal));
        Assert.NotNull(upsert.Properties);
        Assert.Contains("OrderId", upsert.Properties, StringComparer.OrdinalIgnoreCase);

        var mismatches = new List<string>();
        foreach (var binding in bindings)
        {
            var parameters = SqlParameters.Required(binding.Sql);
            if (parameters.Count == 0)
                continue;

            if (binding.Properties is null)
            {
                mismatches.Add(
                    $"{binding.File}:{binding.Line} používá {Format(parameters)}, ale nepředává objekt s parametry.");
                continue;
            }

            foreach (var parameter in parameters)
            {
                if (binding.Properties.Contains(parameter, StringComparer.OrdinalIgnoreCase))
                    continue;

                mismatches.Add(
                    $"{binding.File}:{binding.Line} @{parameter} nemá odpovídající vlastnost na {{{string.Join(", ", binding.Properties)}}}.");
            }
        }

        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches));
    }

    private static string Format(IReadOnlyList<string> parameters) =>
        string.Join(", ", parameters.Select(parameter => "@" + parameter));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Dispatch", "SqlOrderStore.cs")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Nenalezen kořen repozitáře s Dispatch/SqlOrderStore.cs.");
    }

    private sealed record DapperBinding(string File, int Line, string Sql, IReadOnlyList<string>? Properties);

    private static class DapperCallScanner
    {
        public static IReadOnlyList<DapperBinding> Scan(string source, string file)
        {
            var constants = ReadConstStrings(source);
            var declarations = ReadDeclarations(source);
            var bindings = new List<DapperBinding>();

            for (var i = 0; i < source.Length;)
            {
                if (TrySkipNonCode(source, ref i))
                    continue;

                if (!TryReadIdentifier(source, i, out var identifier, out var after))
                {
                    i++;
                    continue;
                }

                var open = NextNonWhitespace(source, after);
                var isCommand = identifier == "Command" && PreviousNonWhitespace(source, i) == '(';
                var isDefinition = identifier == "CommandDefinition" && PreviousIdentifier(source, i) == "new";
                if ((!isCommand && !isDefinition) || open < 0 || source[open] != '(')
                {
                    i = after;
                    continue;
                }

                var close = FindMatch(source, open, '(', ')');
                var arguments = SplitTopLevel(source, open + 1, close, ',');
                bindings.Add(ParseCall(source, file, i, isDefinition, arguments, constants, declarations));
                i = close + 1;
            }

            return bindings;
        }

        private static DapperBinding ParseCall(
            string source,
            string file,
            int index,
            bool commandDefinition,
            IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string> constants,
            IReadOnlyList<Declaration> declarations)
        {
            var positional = new List<string>();
            var named = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var argument in arguments)
            {
                var trimmed = argument.Trim();
                if (TryNamedArgument(trimmed, out var name, out var value))
                    named[name] = value;
                else
                    positional.Add(trimmed);
            }

            var sqlName = commandDefinition ? "commandText" : "sql";
            var sqlExpression = named.TryGetValue(sqlName, out var namedSql)
                ? namedSql
                : positional.ElementAtOrDefault(0);
            if (sqlExpression is null || !TryEvalString(sqlExpression.Trim(), constants, out var sql))
                throw new InvalidOperationException(
                    $"{file}:{LineOf(source, index)} má Dapper volání, jehož SQL se nepodařilo vyhodnotit: {sqlExpression}");

            string? parameterExpression = null;
            if (named.TryGetValue("parameters", out var namedParameters))
                parameterExpression = namedParameters;
            else if (commandDefinition && positional.Count >= 2)
                parameterExpression = positional[1];
            else if (!commandDefinition && positional.Count >= 4)
                parameterExpression = positional[3];

            IReadOnlyList<string>? properties = null;
            if (parameterExpression is not null && !string.Equals(parameterExpression.Trim(), "null", StringComparison.Ordinal))
                properties = PropertiesOf(parameterExpression.Trim(), index, declarations);

            return new DapperBinding(file, LineOf(source, index), sql, properties);
        }

        private static bool TryNamedArgument(string argument, out string name, out string value)
        {
            name = "";
            value = "";
            var colon = IndexAtDepthZero(argument, ':');
            if (colon <= 0)
                return false;

            var left = argument[..colon].Trim();
            if (!IsIdentifier(left))
                return false;

            name = left;
            value = argument[(colon + 1)..];
            return true;
        }

        private static IReadOnlyList<string> PropertiesOf(
            string expression,
            int callIndex,
            IReadOnlyList<Declaration> declarations)
        {
            if (expression.StartsWith("new", StringComparison.Ordinal) &&
                (expression.Length == 3 || !IsIdentifierChar(expression[3])))
            {
                var rest = expression[3..].TrimStart();
                if (rest.StartsWith('{'))
                    return AnonymousProperties(rest);

                throw new InvalidOperationException($"Nepodporovaný parametrový výraz: {expression}");
            }

            if (!IsIdentifier(expression))
                throw new InvalidOperationException($"Nepodporovaný parametrový výraz: {expression}");

            var declaration = declarations.LastOrDefault(item =>
                item.Index < callIndex && string.Equals(item.Name, expression, StringComparison.Ordinal));
            if (declaration is null)
                throw new InvalidOperationException($"U volání chybí deklarace parametru '{expression}'.");

            var type = typeof(SqlOrderStore).Assembly.GetTypes().SingleOrDefault(candidate =>
                string.Equals(candidate.Name, declaration.Type, StringComparison.Ordinal) &&
                candidate.Namespace?.StartsWith("PHMTRANS", StringComparison.Ordinal) == true);
            if (type is null)
                throw new InvalidOperationException($"Typ '{declaration.Type}' pro parametr '{expression}' není v sestavení.");

            return type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Select(property => property.Name)
                .ToArray();
        }

        private static IReadOnlyList<string> AnonymousProperties(string expression)
        {
            var close = FindMatch(expression, 0, '{', '}');
            var members = SplitTopLevel(expression, 1, close, ',');
            var names = new List<string>();
            foreach (var member in members)
            {
                var trimmed = member.Trim();
                if (trimmed.Length == 0)
                    continue;

                var equals = IndexOfAssignment(trimmed);
                var left = equals >= 0 ? trimmed[..equals] : trimmed;
                names.Add(LastIdentifier(left));
            }

            return names;
        }

        private static int IndexOfAssignment(string text)
        {
            for (var i = 0; i < text.Length;)
            {
                if (TrySkipNonCode(text, ref i))
                    continue;

                var depth = DepthAt(text, i);
                if (depth == 0 && text[i] == '=' && (i == 0 || text[i - 1] is not '=' and not '!' and not '<' and not '>') &&
                    (i + 1 >= text.Length || text[i + 1] != '='))
                    return i;

                i++;
            }

            return -1;
        }

        private static int DepthAt(string text, int index)
        {
            var depth = 0;
            for (var i = 0; i < index;)
            {
                if (TrySkipNonCode(text, ref i))
                    continue;

                depth += text[i] switch
                {
                    '(' or '[' or '{' => 1,
                    ')' or ']' or '}' => -1,
                    _ => 0
                };
                i++;
            }

            return depth;
        }

        private static string LastIdentifier(string text)
        {
            string? last = null;
            for (var i = 0; i < text.Length;)
            {
                if (TrySkipNonCode(text, ref i))
                    continue;

                if (TryReadIdentifier(text, i, out var identifier, out var after))
                {
                    last = identifier;
                    i = after;
                    continue;
                }

                i++;
            }

            if (last is null)
                throw new InvalidOperationException($"V anonymním členu '{text}' chybí název vlastnosti.");

            return last;
        }

        private static bool TryEvalString(string expression, IReadOnlyDictionary<string, string> constants, out string value)
        {
            var parts = SplitTopLevel(expression, 0, expression.Length, '+');
            if (parts.Count == 0)
            {
                value = "";
                return false;
            }

            var builder = new System.Text.StringBuilder();
            foreach (var part in parts)
            {
                if (!TryEvalStringPart(part.Trim(), constants, out var piece))
                {
                    value = "";
                    return false;
                }

                builder.Append(piece);
            }

            value = builder.ToString();
            return true;
        }

        private static bool TryEvalStringPart(string expression, IReadOnlyDictionary<string, string> constants, out string value)
        {
            if (expression.StartsWith("\"\"\"", StringComparison.Ordinal))
            {
                value = DecodeRaw(expression);
                return true;
            }

            if (expression.StartsWith("@\"", StringComparison.Ordinal))
            {
                value = DecodeVerbatim(expression);
                return true;
            }

            if (expression.StartsWith('"'))
            {
                value = DecodeRegular(expression);
                return true;
            }

            if (IsIdentifier(expression) && constants.TryGetValue(expression, out var constant))
            {
                value = constant;
                return true;
            }

            value = "";
            return false;
        }

        private static string DecodeRaw(string expression)
        {
            var quotes = 0;
            while (quotes < expression.Length && expression[quotes] == '"')
                quotes++;

            var close = expression.LastIndexOf(new string('"', quotes), StringComparison.Ordinal);
            if (close <= quotes)
                throw new InvalidOperationException("Neuzavřený raw string.");

            return expression[quotes..close];
        }

        private static string DecodeVerbatim(string expression)
        {
            var body = expression[2..];
            if (body.EndsWith('"'))
                body = body[..^1];
            return body.Replace("\"\"", "\"", StringComparison.Ordinal);
        }

        private static string DecodeRegular(string expression)
        {
            var body = expression[1..];
            if (body.EndsWith('"'))
                body = body[..^1];

            var builder = new System.Text.StringBuilder(body.Length);
            for (var i = 0; i < body.Length; i++)
            {
                if (body[i] != '\\' || i + 1 >= body.Length)
                {
                    builder.Append(body[i]);
                    continue;
                }

                builder.Append(body[++i] switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '\\' => '\\',
                    '"' => '"',
                    '\'' => '\'',
                    '0' => '\0',
                    var other => other
                });
            }

            return builder.ToString();
        }

        private static Dictionary<string, string> ReadConstStrings(string source)
        {
            var constants = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < source.Length;)
            {
                if (TrySkipNonCode(source, ref i))
                    continue;

                if (!TryReadIdentifier(source, i, out var identifier, out var after))
                {
                    i++;
                    continue;
                }

                if (identifier != "const")
                {
                    i = after;
                    continue;
                }

                var typeAt = NextNonWhitespace(source, after);
                if (typeAt < 0 || !TryReadIdentifier(source, typeAt, out var type, out var afterType) || type != "string")
                {
                    i = after;
                    continue;
                }

                var nameAt = NextNonWhitespace(source, afterType);
                if (nameAt < 0 || !TryReadIdentifier(source, nameAt, out var name, out var afterName))
                {
                    i = after;
                    continue;
                }

                var equals = NextNonWhitespace(source, afterName);
                if (equals < 0 || source[equals] != '=')
                {
                    i = after;
                    continue;
                }

                var semicolon = FindTopLevel(source, equals + 1, ';');
                var expression = source[(equals + 1)..semicolon];
                if (!TryEvalString(expression.Trim(), constants, out var value))
                    throw new InvalidOperationException($"Konstanta {name} není složená ze stringů.");

                constants[name] = value;
                i = semicolon + 1;
            }

            return constants;
        }

        private static List<Declaration> ReadDeclarations(string source)
        {
            var declarations = new List<Declaration>();
            for (var i = 0; i < source.Length;)
            {
                if (TrySkipNonCode(source, ref i))
                    continue;

                if (!TryReadIdentifier(source, i, out var type, out var afterType))
                {
                    i++;
                    continue;
                }

                if (IsKeyword(type))
                {
                    i = afterType;
                    continue;
                }

                var cursor = afterType;
                if (cursor < source.Length && source[cursor] == '?')
                    cursor++;

                var nameAt = cursor;
                if (nameAt >= source.Length || !char.IsWhiteSpace(source[nameAt]))
                {
                    i = afterType;
                    continue;
                }

                nameAt = NextNonWhitespace(source, nameAt);
                if (nameAt < 0 || !TryReadIdentifier(source, nameAt, out var name, out var afterName) ||
                    (!char.IsLower(name[0]) && name[0] != '_'))
                {
                    i = afterType;
                    continue;
                }

                var follower = NextNonWhitespace(source, afterName);
                if (follower >= 0 && source[follower] is ',' or ')' or '=' or ';')
                    declarations.Add(new Declaration(nameAt, type, name));

                i = afterName;
            }

            return declarations;
        }

        private static bool IsKeyword(string identifier) => identifier is
            "if" or "for" or "foreach" or "while" or "switch" or "catch" or "return" or "new" or "await" or
            "var" or "const" or "class" or "record" or "struct" or "namespace" or "using" or "public" or
            "private" or "protected" or "internal" or "static" or "sealed" or "abstract" or "async" or
            "override" or "void" or "true" or "false" or "null" or "this" or "base" or "typeof" or "nameof" or
            "where" or "select" or "from" or "orderby" or "group" or "into" or "join" or "let" or "equals" or
            "throw" or "try" or "else" or "case" or "when" or "lock" or "fixed" or "out" or "ref" or "in" or
            "params" or "readonly" or "required" or "file" or "partial" or "event" or "delegate" or "operator" or
            "get" or "set" or "init" or "add" or "remove" or "global" or "not" or "and" or "or" or "is" or "as";

        private static List<string> SplitTopLevel(string source, int start, int end, char separator)
        {
            var parts = new List<string>();
            var from = start;
            for (var i = start; i < end;)
            {
                if (TrySkipNonCode(source, ref i))
                    continue;

                if (i < end && DepthBetween(source, start, i) == 0 && source[i] == separator)
                {
                    parts.Add(source[from..i]);
                    from = i + 1;
                }

                i++;
            }

            if (from < end)
                parts.Add(source[from..end]);
            else if (from == end && parts.Count > 0 && separator == ',')
                parts.Add("");

            return parts;
        }

        private static int DepthBetween(string source, int start, int index)
        {
            var depth = 0;
            for (var i = start; i < index;)
            {
                if (TrySkipNonCode(source, ref i))
                    continue;

                if (i >= index)
                    break;

                depth += source[i] switch
                {
                    '(' or '[' or '{' => 1,
                    ')' or ']' or '}' => -1,
                    _ => 0
                };
                i++;
            }

            return depth;
        }

        private static int IndexAtDepthZero(string text, char token)
        {
            for (var i = 0; i < text.Length;)
            {
                if (TrySkipNonCode(text, ref i))
                    continue;

                if (DepthBetween(text, 0, i) == 0 && text[i] == token)
                    return i;

                i++;
            }

            return -1;
        }

        private static int FindTopLevel(string source, int start, char token)
        {
            for (var i = start; i < source.Length;)
            {
                if (TrySkipNonCode(source, ref i))
                    continue;

                if (DepthBetween(source, start, i) == 0 && source[i] == token)
                    return i;

                i++;
            }

            throw new InvalidOperationException($"V kódu chybí '{token}'.");
        }

        private static int FindMatch(string source, int open, char opener, char closer)
        {
            var depth = 0;
            for (var i = open; i < source.Length;)
            {
                if (TrySkipNonCode(source, ref i))
                    continue;

                if (source[i] == opener)
                    depth++;
                else if (source[i] == closer && --depth == 0)
                    return i;

                i++;
            }

            throw new InvalidOperationException($"Chybí uzavírací '{closer}'.");
        }

        private static int NextNonWhitespace(string source, int index)
        {
            for (var i = index; i < source.Length;)
            {
                if (TrySkipNonCode(source, ref i))
                    continue;

                if (char.IsWhiteSpace(source[i]))
                {
                    i++;
                    continue;
                }

                return i;
            }

            return -1;
        }

        private static char PreviousNonWhitespace(string source, int index)
        {
            for (var i = index - 1; i >= 0; i--)
            {
                if (char.IsWhiteSpace(source[i]))
                    continue;
                return source[i];
            }

            return '\0';
        }

        private static string? PreviousIdentifier(string source, int index)
        {
            var i = index - 1;
            while (i >= 0 && char.IsWhiteSpace(source[i]))
                i--;

            if (i < 0 || !IsIdentifierChar(source[i]))
                return null;

            var end = i + 1;
            while (i >= 0 && IsIdentifierChar(source[i]))
                i--;

            return source[(i + 1)..end];
        }

        private static bool TryReadIdentifier(string source, int index, out string identifier, out int after)
        {
            identifier = "";
            after = index;
            if (index >= source.Length || !IsIdentifierStart(source[index]))
                return false;

            var end = index + 1;
            while (end < source.Length && IsIdentifierChar(source[end]))
                end++;

            identifier = source[index..end];
            after = end;
            return true;
        }

        private static bool IsIdentifier(string text)
        {
            if (text.Length == 0 || !IsIdentifierStart(text[0]))
                return false;

            for (var i = 1; i < text.Length; i++)
            {
                if (!IsIdentifierChar(text[i]))
                    return false;
            }

            return true;
        }

        private static bool IsIdentifierStart(char value) => char.IsLetter(value) || value == '_';

        private static bool IsIdentifierChar(char value) => char.IsLetterOrDigit(value) || value == '_';

        private static bool TrySkipNonCode(string source, ref int index)
        {
            if (index >= source.Length)
                return false;

            if (source[index] == '/' && index + 1 < source.Length && source[index + 1] == '/')
            {
                index = source.IndexOf('\n', index);
                if (index < 0)
                    index = source.Length;
                return true;
            }

            if (source[index] == '/' && index + 1 < source.Length && source[index + 1] == '*')
            {
                var end = source.IndexOf("*/", index + 2, StringComparison.Ordinal);
                index = end < 0 ? source.Length : end + 2;
                return true;
            }

            if (source[index] == '\'')
            {
                index++;
                if (index < source.Length && source[index] == '\\')
                    index++;
                index = Math.Min(source.Length, index + 2);
                return true;
            }

            if (source[index] == '$' && index + 1 < source.Length && (source[index + 1] == '"' || source[index + 1] == '@'))
            {
                index = SkipInterpolated(source, index);
                return true;
            }

            if (source[index] == '@' && index + 1 < source.Length && source[index + 1] == '"')
            {
                index = SkipVerbatim(source, index + 2);
                return true;
            }

            if (source[index] == '"')
            {
                index = SkipString(source, index);
                return true;
            }

            return false;
        }

        private static int SkipString(string source, int index)
        {
            var quotes = 0;
            while (index + quotes < source.Length && source[index + quotes] == '"')
                quotes++;

            if (quotes >= 3)
            {
                var close = source.IndexOf(new string('"', quotes), index + quotes, StringComparison.Ordinal);
                if (close < 0)
                    throw new InvalidOperationException("Neuzavřený raw string.");
                return close + quotes;
            }

            index++;
            while (index < source.Length)
            {
                if (source[index] == '\\')
                {
                    index += 2;
                    continue;
                }

                if (source[index] == '"')
                    return index + 1;

                index++;
            }

            throw new InvalidOperationException("Neuzavřený string.");
        }

        private static int SkipVerbatim(string source, int index)
        {
            while (index < source.Length)
            {
                if (source[index] == '"')
                {
                    if (index + 1 < source.Length && source[index + 1] == '"')
                    {
                        index += 2;
                        continue;
                    }

                    return index + 1;
                }

                index++;
            }

            throw new InvalidOperationException("Neuzavřený verbatim string.");
        }

        private static int SkipInterpolated(string source, int index)
        {
            index++;
            if (index < source.Length && source[index] == '@')
                index++;

            var raw = index < source.Length && index + 2 < source.Length && source[index] == '"' && source[index + 1] == '"' && source[index + 2] == '"';
            if (source[index] != '"')
                return index;

            if (raw)
            {
                var quotes = 0;
                while (index + quotes < source.Length && source[index + quotes] == '"')
                    quotes++;
                index += quotes;
                var delimiter = new string('"', quotes);
                while (index < source.Length)
                {
                    if (source[index] == '{')
                    {
                        if (index + 1 < source.Length && source[index + 1] == '{')
                        {
                            index += 2;
                            continue;
                        }

                        index = SkipHole(source, index + 1);
                        continue;
                    }

                    if (source.AsSpan(index).StartsWith(delimiter))
                        return index + quotes;

                    index++;
                }

                throw new InvalidOperationException("Neuzavřený interpolovaný raw string.");
            }

            index++;
            while (index < source.Length)
            {
                if (source[index] == '\\')
                {
                    index += 2;
                    continue;
                }

                if (source[index] == '{')
                {
                    if (index + 1 < source.Length && source[index + 1] == '{')
                    {
                        index += 2;
                        continue;
                    }

                    index = SkipHole(source, index + 1);
                    continue;
                }

                if (source[index] == '"')
                    return index + 1;

                index++;
            }

            throw new InvalidOperationException("Neuzavřený interpolovaný string.");
        }

        private static int SkipHole(string source, int index)
        {
            var depth = 1;
            while (index < source.Length && depth > 0)
            {
                if (TrySkipNonCode(source, ref index))
                    continue;

                if (source[index] == '{')
                    depth++;
                else if (source[index] == '}')
                    depth--;

                index++;
            }

            return index;
        }

        private static int LineOf(string source, int index) =>
            1 + source.Take(Math.Max(0, index)).Count(character => character == '\n');

        private sealed record Declaration(int Index, string Type, string Name);
    }

    private static class SqlParameters
    {
        private static readonly Regex Parameter = new(@"(?<!@)@([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);
        private static readonly Regex Declare = new(@"\bDECLARE\b([^;]*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex Execute = new(@"\bEXEC(?:UTE)?\b([^;]*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex Name = new(@"@([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);

        public static IReadOnlyList<string> Required(string sql)
        {
            var cleaned = Strip(sql);
            var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match statement in Declare.Matches(cleaned))
            {
                foreach (Match variable in Name.Matches(statement.Groups[1].Value))
                    excluded.Add(variable.Groups[1].Value);
            }

            foreach (Match statement in Execute.Matches(cleaned))
            {
                var body = statement.Groups[1].Value;
                var returned = Regex.Match(body, @"^\s*@([A-Za-z_][A-Za-z0-9_]*)\s*=");
                if (returned.Success)
                {
                    excluded.Add(returned.Groups[1].Value);
                    body = body[(returned.Index + returned.Length)..];
                }

                var procedure = Regex.Match(body, @"^\s*\[?[\w.]+\]?");
                if (procedure.Success)
                    body = body[procedure.Length..];

                foreach (Match argument in Regex.Matches(body, @"@([A-Za-z_][A-Za-z0-9_]*)\s*="))
                    excluded.Add(argument.Groups[1].Value);
            }

            var required = new List<string>();
            foreach (Match match in Parameter.Matches(cleaned))
            {
                var name = match.Groups[1].Value;
                if (excluded.Contains(name) || required.Contains(name, StringComparer.OrdinalIgnoreCase))
                    continue;

                required.Add(name);
            }

            return required;
        }

        private static string Strip(string sql)
        {
            var builder = new System.Text.StringBuilder(sql.Length);
            for (var i = 0; i < sql.Length; i++)
            {
                if (sql[i] == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
                {
                    while (i < sql.Length && sql[i] != '\n')
                        i++;
                    continue;
                }

                if (sql[i] == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < sql.Length && !(sql[i] == '*' && sql[i + 1] == '/'))
                        i++;
                    i++;
                    continue;
                }

                if (sql[i] == '\'')
                {
                    i++;
                    while (i < sql.Length)
                    {
                        if (sql[i] == '\'' && i + 1 < sql.Length && sql[i + 1] == '\'')
                        {
                            i += 2;
                            continue;
                        }

                        if (sql[i] == '\'')
                            break;

                        i++;
                    }

                    builder.Append("''");
                    continue;
                }

                builder.Append(sql[i]);
            }

            return builder.ToString();
        }
    }
}
