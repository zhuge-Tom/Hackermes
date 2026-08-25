using Hackermes.AiPanel.OpenAI;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.AiPanel.Runtime;

/// <summary>
/// Input to one pre-step interception point (dsh agent/pre-step waterfall lineage): the
/// turn/step coordinates plus the user-visible messages claimed for this step (steering,
/// goal rounds, injected contexts — empty for a plain continuation).
/// </summary>
public sealed record PreStepInput(int Turn, int Step, IReadOnlyList<ChatMessage> EnteringMessages);

/// <summary>Outcome of the pre-step waterfall.</summary>
public abstract record PreStepDecision
{
    /// <summary>Singleton allow: proceed unchanged.</summary>
    public static readonly PreStepDecision Proceed = new ProceedDecision();

    /// <summary>
    /// Appends ephemeral messages to THIS request only (after the assembled context, before
    /// sending). They never enter persistent history — the seam for page-snapshot injection,
    /// assessment context attachment and similar per-step enrichment.
    /// </summary>
    public static PreStepDecision AppendEphemeral(IReadOnlyList<ChatMessage> appendix) =>
        new EphemeralDecision(appendix);

    /// <summary>
    /// Rewrites the claimed entering messages before they are appended to history.
    /// The redaction seam: sensitive-parameter scrubbing and similar normalization.
    /// </summary>
    public static PreStepDecision RewriteEntering(IReadOnlyList<ChatMessage> rewritten) =>
        new RewriteDecision(rewritten);

    /// <summary>
    /// Rejects the step outright: the turn closes as Blocked without spending a model call;
    /// the log records the attempt. Claimed messages are consumed with it (dsh semantics).
    /// </summary>
    public static PreStepDecision Reject(string reason) => new RejectDecision(reason);

    public sealed record ProceedDecision : PreStepDecision;
    public sealed record EphemeralDecision(IReadOnlyList<ChatMessage> Appendix) : PreStepDecision;
    public sealed record RewriteDecision(IReadOnlyList<ChatMessage> Rewritten) : PreStepDecision;
    public sealed record RejectDecision(string Reason) : PreStepDecision;

    private PreStepDecision() { }
}

/// <summary>
/// One interception point evaluated before every model step, in registration order
/// (waterfall): the first Reject wins; rewrites chain across hooks; ephemeral appendices
/// from all hooks accumulate. Throwing hooks are contained by the runner like any listener.
/// </summary>
public interface IAgentPreStepHook
{
    ValueTask<PreStepDecision> BeforeStepAsync(PreStepInput input, CancellationToken ct);
}
