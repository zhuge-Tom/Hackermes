using Hackermes.Automation.Commands;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Hackermes.App;

/// <summary>
/// Operator identity management surface. Deliberately CLI-only, like signing-keys:
/// choosing who signs the audit chain is an operator decision.
/// </summary>
internal static class IdentityCommandRegistrar
{
    public static void Register(CommandRegistry commands, OperatorIdentityDirectory directory)
    {
        commands.Register(new CommandDefinition
        {
            Name = "identity",
            Summary = "Manage local operator identities stamped into the traffic audit chain",
            Usage = "identity list | adopt <name> | use <name-or-id>",
            IsMutating = true,
            Handler = (context, _) => Task.FromResult(Execute(context, directory))
        });
    }

    private static CommandResult Execute(CommandContext context, OperatorIdentityDirectory directory)
    {
        try
        {
            switch (context.Arg(0)?.ToLowerInvariant())
            {
                case "list":
                    return CommandResult.Ok(Format(directory));
                case "adopt":
                {
                    var identity = directory.Adopt(Required(context, 1, "name"));
                    return CommandResult.Ok($"adopted name={identity.Name} id={identity.Id} active=yes");
                }
                case "use":
                    return directory.Use(Required(context, 1, "name-or-id"))
                        ? CommandResult.Ok($"active={directory.ResolveActiveName()}")
                        : CommandResult.Fail("Unknown identity; nothing was switched.");
                default:
                    return CommandResult.Fail("Usage: identity list | adopt <name> | use <name-or-id>");
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException)
        {
            return CommandResult.Fail(exception.Message);
        }
    }

    private static string Format(OperatorIdentityDirectory directory)
    {
        var identities = directory.Identities;
        var header = $"resolved={directory.ResolveActiveName() ?? "(fallback: traffic.operatorName or environment user)"}";
        if (identities.Count == 0) return header;
        var lines = identities.Select(identity =>
            $"{(identity.Id == directory.ActiveId ? "*" : " ")} {identity.Name} ({identity.Id}) created={identity.CreatedAtUtc:O}");
        return header + Environment.NewLine + string.Join(Environment.NewLine, lines);
    }

    private static string Required(CommandContext context, int index, string name) =>
        context.Arg(index) ?? throw new ArgumentException($"Missing {name}.");
}
