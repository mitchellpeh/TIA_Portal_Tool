namespace TiaPortalTool.Conversion.Slc;

/// <summary>One element of a rung: an instruction with its operands, or a branch marker (BST/NXB/BND).</summary>
public sealed class RungElement
{
    public RungElement(string mnemonic, IReadOnlyList<string> operands, SlcInstructionInfo? info)
    {
        Mnemonic = mnemonic;
        Operands = operands;
        Info = info;
    }

    public string Mnemonic { get; }
    public IReadOnlyList<string> Operands { get; }
    public SlcInstructionInfo? Info { get; }
    public bool IsBranch => SlcInstructionSet.IsBranchToken(Mnemonic);

    public override string ToString() => Operands.Count == 0 ? Mnemonic : Mnemonic + " " + string.Join(" ", Operands);
}

public sealed class TokenizedRung
{
    public List<RungElement> Elements { get; } = new();

    /// <summary>Why the rung couldn't be fully split into instructions, or null if it could.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// Splits a rung's mnemonic text ("SOR XIC B3:0/0 BST OTE O:2.0/1 NXB ... BND EOR") into instructions using the
/// operand counts in <see cref="SlcInstructionSet"/>. Branch structure is kept as BST/NXB/BND markers.
/// </summary>
public static class RungTokenizer
{
    // Binary operators that can follow an operand inside a CPT expression. ("|" is divide.)
    private static readonly HashSet<string> ExpressionOperators = new(StringComparer.Ordinal)
    {
        "+", "-", "*", "|", "**", "AND", "OR", "XOR", "MOD"
    };

    public static TokenizedRung Tokenize(string text, bool microLogix)
    {
        var result = new TokenizedRung();
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var i = 0;

        if (tokens.Length == 0 || tokens[0] != "SOR")
        {
            result.Error = "rung doesn't start with SOR";
            return result;
        }

        i++;
        while (i < tokens.Length && tokens[i] != "EOR")
        {
            var token = tokens[i];
            if (SlcInstructionSet.IsBranchToken(token))
            {
                result.Elements.Add(new RungElement(token, Array.Empty<string>(), null));
                i++;
                continue;
            }

            var info = SlcInstructionSet.Find(token);
            if (info is null)
            {
                result.Error = $"unknown instruction '{token}'";
                return result;
            }

            i++;
            List<string> operands;
            if (info.Operands < 0)
            {
                operands = ReadCptOperands(tokens, ref i);
            }
            else
            {
                var count = SlcInstructionSet.OperandCount(info, microLogix);
                if (i + count > tokens.Length)
                {
                    result.Error = $"{token} is missing operands";
                    return result;
                }

                operands = tokens.Skip(i).Take(count).ToList();
                i += count;
            }

            result.Elements.Add(new RungElement(token, operands, info));
        }

        if (i >= tokens.Length)
        {
            result.Error = "rung doesn't end with EOR";
        }

        return result;
    }

    /// <summary>
    /// CPT takes a destination and an expression of operands, operators and parentheses. The expression ends at the
    /// first token that can't continue it: after a complete operand, anything that isn't a binary operator or ")".
    /// </summary>
    private static List<string> ReadCptOperands(string[] tokens, ref int i)
    {
        var operands = new List<string>();
        if (i < tokens.Length)
        {
            operands.Add(tokens[i++]);
        }

        var expression = new List<string>();
        var depth = 0;
        var expectOperand = true;
        while (i < tokens.Length && tokens[i] != "EOR")
        {
            var token = tokens[i];
            if (expectOperand)
            {
                if (token == "(")
                {
                    depth++;
                }
                else if (!IsUnaryFunction(token))
                {
                    expectOperand = false;
                }
            }
            else if (token == ")" && depth > 0)
            {
                depth--;
            }
            else if (ExpressionOperators.Contains(token))
            {
                expectOperand = true;
            }
            else if (depth == 0)
            {
                break;
            }

            expression.Add(token);
            i++;
        }

        operands.Add(string.Join(" ", expression));
        return operands;
    }

    private static bool IsUnaryFunction(string token) => token is "-" or "NOT" or "SQR" or "ABS" or "FRD" or "TOD" or "LN" or "LOG"
        or "SIN" or "COS" or "TAN" or "ASN" or "ACS" or "ATN" or "DEG" or "RAD";
}
