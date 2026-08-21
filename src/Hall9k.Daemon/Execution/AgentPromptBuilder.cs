using System.Text;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Features.Tasks.Queries;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// Assembles the agent's prompt from the readiness contract (PLAN.md §4): objective,
/// acceptance criteria, agent context, the project's context links, and — when the
/// worktree ships repo skills — a one-line pointer per skill. Pointers, not pasted
/// content — the agent pulls context itself and skills load on invocation.
/// </summary>
public static class AgentPromptBuilder
{
    public static string Build(
        TaskDetails task,
        ProjectDetails project,
        string branch,
        string worktreePath,
        bool resumesPreviousWork = false,
        string? blockerContext = null)
    {
        StringBuilder prompt = new();
        prompt.AppendLine("# Task");
        prompt.AppendLine();
        prompt.AppendLine(task.Objective);
        prompt.AppendLine();

        if (resumesPreviousWork)
        {
            // A retry resumes the failed run's branch, and the retained worktree carries
            // whatever that attempt left — including uncommitted work (origin incident,
            // 2026-08-18: gen 2-4 of a review-parked task each rebuilt the same feature
            // from scratch instead of finding the finished work already in the worktree).
            prompt.AppendLine("## A previous attempt worked here first");
            prompt.AppendLine();
            prompt.AppendLine("This run retries a failed attempt and resumes its branch in its retained");
            prompt.AppendLine("worktree. The previous attempt's work may already be present — committed on the");
            prompt.AppendLine("branch, uncommitted in the working tree, or both. Before writing anything, review");
            prompt.AppendLine("what is there (`git status`, `git log`, `git diff`), judge it against the");
            prompt.AppendLine("acceptance criteria, and continue from it. Do not start over when usable work");
            prompt.AppendLine("exists; redoing finished work is the failure mode this note exists to prevent.");
            prompt.AppendLine();
        }

        prompt.AppendLine("## Acceptance criteria");
        prompt.AppendLine();
        foreach (string criterion in task.AcceptanceCriteria)
        {
            prompt.AppendLine($"- {criterion}");
        }

        prompt.AppendLine();

        if (task.AgentContext.IsNotBlank())
        {
            prompt.AppendLine("## Context");
            prompt.AppendLine();
            prompt.AppendLine(task.AgentContext);
            prompt.AppendLine();
        }

        // What this task's immediate blockers handed down (Decisions Log #36). It sits after
        // the task's own context and before the project links: nearer than a link the agent
        // may or may not fetch, and never mistaken for part of the objective.
        if (blockerContext.IsNotBlank())
        {
            prompt.AppendLine(blockerContext);
            prompt.AppendLine();
        }

        if (project.ContextLinks.Count > 0)
        {
            prompt.AppendLine("## Project links (fetch yourself as needed)");
            prompt.AppendLine();
            foreach (var link in project.ContextLinks)
            {
                prompt.AppendLine($"- {link.Name}: {link.Url}");
            }

            prompt.AppendLine();
        }

        prompt.AppendLine("## Working rules");
        prompt.AppendLine();
        prompt.AppendLine($"- You are in an isolated git worktree on branch `{branch}`. Work only here.");
        prompt.AppendLine("- Implement the objective so every acceptance criterion is satisfied.");
        prompt.AppendLine("- Commit your work with clear messages. Do NOT push, do NOT open a pull request —");
        prompt.AppendLine("  the platform verifies and opens the PR after you finish.");

        IReadOnlyList<RepoSkill> skills = DiscoverRepoSkills(worktreePath);
        if (skills.Count > 0)
        {
            prompt.AppendLine("- This repo ships Claude skills; invoke the matching one instead of improvising its workflow:");
            foreach (RepoSkill skill in skills)
            {
                prompt.AppendLine(skill.Description is null
                    ? $"  - `{skill.Name}`"
                    : $"  - `{skill.Name}` — {skill.Description}");
            }
        }

        prompt.AppendLine("- If something is genuinely ambiguous, make the most reasonable choice and record");
        prompt.AppendLine("  the assumption in your final summary (the ask-a-human loop is not available yet).");
        prompt.AppendLine("- End with a short summary: what you did, decisions made, assumptions, open questions.");
        AppendHandoffRules(prompt);

        return prompt.ToString();
    }

    /// <summary>
    /// The handoff the run leaves for whatever depends on it (Decisions Log #36). It is asked
    /// for here, of the agent that did the work, because that agent is the one that knows what
    /// it deliberately left undone — a separate summarizer session would cost more and know
    /// less. The daemon reads this block off the session's own result at session end and holds
    /// it until the pull request merges; a run whose work never lands hands nothing down.
    /// <para>
    /// Brevity is instructed rather than merely enforced: the event that carries this text is
    /// a milestone on the run stream (log #6), and a handoff nobody finishes reading routes no
    /// context at all.
    /// </para>
    /// </summary>
    private static void AppendHandoffRules(StringBuilder prompt)
    {
        prompt.AppendLine();
        prompt.AppendLine("## Handoff (required — the last thing in your final message)");
        prompt.AppendLine();
        prompt.AppendLine("Tasks that depend on this one start with what you write here, and nothing else you");
        prompt.AppendLine("learned survives this session. After your summary, end your final message with a");
        prompt.AppendLine("line reading exactly:");
        prompt.AppendLine();
        prompt.AppendLine($"    {HandoffParser.Marker}");
        prompt.AppendLine();
        prompt.AppendLine("followed by a short handoff — a few sentences or a handful of bullets, not an essay,");
        prompt.AppendLine("and nothing after it. Cover three things:");
        prompt.AppendLine();
        prompt.AppendLine("- What you actually did, in terms of what now exists that did not before.");
        prompt.AppendLine("- What someone building on this needs to know: the gotcha, the non-obvious shape, the");
        prompt.AppendLine("  thing you would tell them in person to save them an hour.");
        prompt.AppendLine("- What you deliberately left undone, and why — so nobody re-litigates a settled call");
        prompt.AppendLine("  or assumes an omission was an oversight.");
        prompt.AppendLine();
        prompt.AppendLine("Write it for someone with no access to this session. If there is genuinely nothing");
        prompt.AppendLine("worth handing down, say so in one line rather than padding it — an honest \"nothing");
        prompt.AppendLine("surprising here\" is useful, and invented significance is not.");
    }

    /// <summary>
    /// The follow-up variant (PR closeout, Decisions Log #20): the agent resumes the task's
    /// existing PR branch to resolve review feedback via the repo-resident
    /// resolve-copilot-reviews skill. How the fixes land is the commit style's call
    /// (Decisions Log #26): narrative folds them into the owning commits, append stacks
    /// them on top. The platform re-verifies and pushes; the PR updates in place.
    /// </summary>
    public static string BuildFollowUp(
        TaskDetails task, ProjectDetails project, string branch, string pullRequestUrl, CommitStyle commitStyle)
    {
        StringBuilder prompt = new();
        prompt.AppendLine("# Follow-up task: resolve review feedback on an existing pull request");
        prompt.AppendLine();
        prompt.AppendLine($"Pull request: {pullRequestUrl}");
        prompt.AppendLine();
        prompt.AppendLine("The original task below already shipped in the pull request above, which now has");
        prompt.AppendLine("unresolved review comments. Your job is to resolve that review feedback — not to");
        prompt.AppendLine("redo the original work.");
        prompt.AppendLine();

        if (task.FollowUpReason.IsNotBlank())
        {
            prompt.AppendLine($"Why this follow-up was dispatched: {task.FollowUpReason}");
            prompt.AppendLine();
        }

        prompt.AppendLine("## Original objective (context, already implemented)");
        prompt.AppendLine();
        prompt.AppendLine(task.Objective);
        prompt.AppendLine();

        if (project.ContextLinks.Count > 0)
        {
            prompt.AppendLine("## Project links (fetch yourself as needed)");
            prompt.AppendLine();
            foreach (var link in project.ContextLinks)
            {
                prompt.AppendLine($"- {link.Name}: {link.Url}");
            }

            prompt.AppendLine();
        }

        prompt.AppendLine("## Working rules");
        prompt.AppendLine();
        prompt.AppendLine("- You are in an isolated git worktree checked out on the EXISTING pull-request");
        prompt.AppendLine($"  branch `{branch}`. Work only here.");
        AppendRetainedWorktreeNote(prompt);
        prompt.AppendLine("- Use the resolve-copilot-reviews skill to triage the review comments on");
        prompt.AppendLine($"  {pullRequestUrl}: apply valid fixes, reply to each thread, resolve them.");
        AppendCommitStyleRules(prompt, commitStyle, project.BaseBranch);
        prompt.AppendLine("- End with a short summary: which comments you addressed, which you dismissed and");
        prompt.AppendLine("  why, and any open questions.");
        // A reopened task's follow-up run is the run that reaches true closeout, so it is the
        // run whose handoff travels (Decisions Log #36) — it covers the whole task, not only
        // this leg's fixes.
        AppendHandoffRules(prompt);

        return prompt.ToString();
    }

    /// <summary>
    /// The fix-the-CI variant (closeout monitor, Decisions Log #22): the agent resumes
    /// the task's existing PR branch to make the pull request's failing checks pass.
    /// Fixes land per the commit style, like any follow-up (Decisions Log #26).
    /// The platform re-verifies and pushes; the PR updates in place.
    /// </summary>
    public static string BuildFixChecks(
        TaskDetails task, ProjectDetails project, string branch, string pullRequestUrl, CommitStyle commitStyle)
    {
        StringBuilder prompt = new();
        prompt.AppendLine("# Follow-up task: fix the failing CI checks on an existing pull request");
        prompt.AppendLine();
        prompt.AppendLine($"Pull request: {pullRequestUrl}");
        prompt.AppendLine();
        prompt.AppendLine("The original task below already shipped in the pull request above, but its CI");
        prompt.AppendLine("checks are failing. Your job is to make the checks pass — not to redo the");
        prompt.AppendLine("original work.");
        prompt.AppendLine();

        if (task.FollowUpReason.IsNotBlank())
        {
            prompt.AppendLine($"Why this follow-up was dispatched: {task.FollowUpReason}");
            prompt.AppendLine();
        }

        prompt.AppendLine("## Original objective (context, already implemented)");
        prompt.AppendLine();
        prompt.AppendLine(task.Objective);
        prompt.AppendLine();

        if (project.ContextLinks.Count > 0)
        {
            prompt.AppendLine("## Project links (fetch yourself as needed)");
            prompt.AppendLine();
            foreach (var link in project.ContextLinks)
            {
                prompt.AppendLine($"- {link.Name}: {link.Url}");
            }

            prompt.AppendLine();
        }

        prompt.AppendLine("## Working rules");
        prompt.AppendLine();
        prompt.AppendLine("- You are in an isolated git worktree checked out on the EXISTING pull-request");
        prompt.AppendLine($"  branch `{branch}`. Work only here.");
        AppendRetainedWorktreeNote(prompt);
        prompt.AppendLine($"- Inspect the failures yourself: `gh pr checks {pullRequestUrl}` lists the checks,");
        prompt.AppendLine("  and `gh run view <run-id> --log-failed` shows a failing workflow's log.");
        prompt.AppendLine("- Fix the causes and re-run the failing commands locally until they pass.");
        AppendCommitStyleRules(prompt, commitStyle, project.BaseBranch);
        prompt.AppendLine("- End with a short summary: what was failing, what you changed, and any open");
        prompt.AppendLine("  questions.");
        AppendHandoffRules(prompt);

        return prompt.ToString();
    }

    /// <summary>
    /// Follow-up runs reuse the previous run's retained worktree (Decisions Log #21),
    /// which by design may carry uncommitted stranded work — a prior session's finished
    /// but never-committed changes (the retained-worktree resume exists exactly so that
    /// work survives). The agent must look before it leaps.
    /// </summary>
    private static void AppendRetainedWorktreeNote(StringBuilder prompt)
    {
        prompt.AppendLine("- This worktree is retained from a previous run and may already hold work from an");
        prompt.AppendLine("  earlier attempt — including UNCOMMITTED changes left in the working tree by");
        prompt.AppendLine("  design. Review `git status` and `git log` before changing anything, and build on");
        prompt.AppendLine("  what is there instead of redoing it.");
    }

    /// <summary>
    /// How a follow-up's fixes land on the PR branch (Decisions Log #26). Narrative
    /// enforces the AGENTS.md authored-history rule: fixups mapped by file ownership,
    /// autosquash onto the base, and a tree-identity check so the verification-gate
    /// results honestly describe the rebased tree the platform will force-push. Append
    /// keeps the historic stack-on-top behavior. Both end the same way: the agent never
    /// pushes; the platform does, with --force-with-lease for follow-up runs.
    /// </summary>
    private static void AppendCommitStyleRules(StringBuilder prompt, CommitStyle commitStyle, string baseBranch)
    {
        if (commitStyle == CommitStyle.Append)
        {
            prompt.AppendLine("- Commit your fixes on this branch with clear messages, on top of the existing");
            prompt.AppendLine("  history (this project uses the append commit style). Do NOT push, do NOT open");
            prompt.AppendLine("  a new pull request — the platform re-verifies and pushes after you finish; the");
            prompt.AppendLine("  existing PR updates in place.");
            return;
        }

        prompt.AppendLine("- Land your fixes as authored history (this project uses the narrative commit");
        prompt.AppendLine("  style): the PR branch must read as a natural progression of the whole change,");
        prompt.AppendLine("  so fold each fix into the commit that owns it instead of appending");
        prompt.AppendLine("  review-feedback commits. If the repo ships an absorb-review-fixes skill, invoke");
        prompt.AppendLine("  it — it walks these exact mechanics. Either way:");
        prompt.AppendLine("  - Map each fix to the most recent branch commit that touches the same file and");
        prompt.AppendLine("    land it with `git commit --fixup=<owning-commit>`. A fix spanning files owned");
        prompt.AppendLine("    by different commits splits into one fixup per owning commit. Genuinely new");
        prompt.AppendLine("    scope (a new file no commit owns) may be a new, properly-titled commit —");
        prompt.AppendLine("    never \"review fixes\" or \"address feedback\".");
        prompt.AppendLine("  - With every fix committed, record the pre-rebase tip (`git rev-parse HEAD`),");
        prompt.AppendLine("    then fold the fixups into their owning commits:");
        prompt.AppendLine($"    `GIT_SEQUENCE_EDITOR=: git rebase -i --autosquash origin/{baseBranch}`.");
        prompt.AppendLine("  - REQUIRED before you finish: verify tree identity — `git diff <old-tip> HEAD`");
        prompt.AppendLine("    must print nothing. A non-empty diff means the rebase changed the content and");
        prompt.AppendLine("    the verification results no longer describe this tree; reconcile until the");
        prompt.AppendLine("    diff is empty. Only a tree identical to the tested one may be force-pushed.");
        prompt.AppendLine("  - Do NOT push (the platform pushes the rewritten branch with");
        prompt.AppendLine("    `git push --force-with-lease` after re-verifying), and do NOT open a new pull");
        prompt.AppendLine("    request — the existing PR updates in place.");
    }

    /// <summary>
    /// The independent pre-PR reviewer (Decisions Log #23): a fresh session that has not
    /// seen the implementation reasoning reviews the branch's diff against the base
    /// before any pull request exists. Verified findings only, and a machine-readable
    /// verdict on the last line — the daemon parses it.
    /// </summary>
    public static string BuildReview(TaskDetails task, ProjectDetails project, string branch, int cycle)
    {
        StringBuilder prompt = new();
        prompt.AppendLine("# Independent review: verify this diff before its pull request opens");
        prompt.AppendLine();
        prompt.AppendLine("You are an independent reviewer with fresh context. A different agent implemented");
        prompt.AppendLine("the task below; you have not seen its reasoning, and that is the point — judge only");
        prompt.AppendLine("the code. No pull request exists yet; your verdict decides whether one opens.");
        prompt.AppendLine();
        prompt.AppendLine("## What the diff is supposed to do");
        prompt.AppendLine();
        prompt.AppendLine(task.Objective);
        prompt.AppendLine();
        prompt.AppendLine("Acceptance criteria:");
        foreach (string criterion in task.AcceptanceCriteria)
        {
            prompt.AppendLine($"- {criterion}");
        }

        prompt.AppendLine();
        prompt.AppendLine("## How to review");
        prompt.AppendLine();
        prompt.AppendLine($"- You are in the implementation's git worktree on branch `{branch}`.");
        prompt.AppendLine($"  The diff under review: `git diff {project.BaseBranch}...HEAD` (commits:");
        prompt.AppendLine($"  `git log {project.BaseBranch}..HEAD`). Use `origin/{project.BaseBranch}` if the local");
        prompt.AppendLine("  base ref is absent.");
        prompt.AppendLine("- Report verified findings only. For every suspected defect, read the surrounding");
        prompt.AppendLine("  code until you can confirm it is real; discard anything you cannot confirm.");
        prompt.AppendLine("- Each finding must carry: the file and line (`path/to/file.cs:123`), a one-sentence");
        prompt.AppendLine("  statement of the defect, and a concrete failure scenario (the input or state that");
        prompt.AppendLine("  makes it misbehave, and what goes wrong).");
        prompt.AppendLine("- Do NOT modify files, commit, push, or open pull requests. You are read-only.");
        prompt.AppendLine();
        prompt.AppendLine("## Verdict (required — never end without it)");
        prompt.AppendLine();
        prompt.AppendLine("End your final message with your findings followed by exactly one verdict line,");
        prompt.AppendLine("nothing after it:");
        prompt.AppendLine();
        prompt.AppendLine("    VERDICT: merge-ready");
        prompt.AppendLine();
        prompt.AppendLine("when you confirmed no defects, or");
        prompt.AppendLine();
        prompt.AppendLine("    VERDICT: needs-fixes");
        prompt.AppendLine();
        prompt.AppendLine("when at least one verified finding stands. You may not end this session without a");
        prompt.AppendLine("VERDICT line. If checks or commands you started are still running, WAIT for them");
        prompt.AppendLine("to finish, then conclude — a promise to deliver the verdict later is not a");
        prompt.AppendLine("verdict, and nobody returns to keep it. The platform parses this line; a missing");
        prompt.AppendLine($"verdict stalls the run and hands it to a human. This is review cycle {cycle} for");
        prompt.AppendLine("this run.");

        return prompt.ToString();
    }

    /// <summary>
    /// The one same-session retry for a reviewer that ended without a VERDICT line: the
    /// session resumes (it already read the diff) and is told to conclude now. One
    /// re-prompt only — a second verdict-less ending parks the run (log #11 spirit).
    /// Origin incident (2026-08-18): the first live review ended with a promise to
    /// deliver the verdict "when it completes" and parked a correct implementation.
    /// </summary>
    public static string BuildReviewVerdictReprompt(int cycle)
    {
        StringBuilder prompt = new();
        prompt.AppendLine("Your review session ended without the required VERDICT line, so the platform");
        prompt.AppendLine("could not read your judgment. Conclude now:");
        prompt.AppendLine();
        prompt.AppendLine("- If any checks or commands are still unfinished, wait for them and fold the");
        prompt.AppendLine("  results into your judgment.");
        prompt.AppendLine("- Restate your verified findings (file:line, defect, failure scenario), or state");
        prompt.AppendLine("  that none stand.");
        prompt.AppendLine("- End your final message with exactly one verdict line, nothing after it:");
        prompt.AppendLine("  `VERDICT: merge-ready` or `VERDICT: needs-fixes`.");
        prompt.AppendLine();
        prompt.AppendLine("This is the only re-prompt you will receive; ending without a verdict again hands");
        prompt.AppendLine($"the run to a human. This is still review cycle {cycle} for this run.");

        return prompt.ToString();
    }

    /// <summary>
    /// The fix leg of the review loop (Decisions Log #23): a fresh session resolves the
    /// reviewer's verified findings in the same worktree. Disputes park for a human
    /// instead of looping — the daemon parses the resolution line.
    /// </summary>
    public static string BuildReviewFix(TaskDetails task, string branch, string findings, int cycle)
    {
        StringBuilder prompt = new();
        prompt.AppendLine("# Fix the verified findings from an independent pre-PR review");
        prompt.AppendLine();
        prompt.AppendLine("An independent reviewer confirmed the defects below in this branch's diff before");
        prompt.AppendLine("its pull request opens. Your job is to resolve those findings — not to redo the");
        prompt.AppendLine("original work, and not to argue with findings you can verify are real.");
        prompt.AppendLine();
        prompt.AppendLine("## Original objective (context, already implemented)");
        prompt.AppendLine();
        prompt.AppendLine(task.Objective);
        prompt.AppendLine();
        prompt.AppendLine($"## Review findings (cycle {cycle})");
        prompt.AppendLine();
        prompt.AppendLine(findings);
        prompt.AppendLine();
        prompt.AppendLine("## Working rules");
        prompt.AppendLine();
        prompt.AppendLine($"- You are in the implementation's git worktree on branch `{branch}`. Work only here.");
        prompt.AppendLine("- Verify each finding yourself, fix the real ones, and commit on this branch with");
        prompt.AppendLine("  clear messages. Do NOT push, do NOT open a pull request — the platform re-runs");
        prompt.AppendLine("  the verification gates and a fresh review after you finish.");
        prompt.AppendLine("- If you judge a finding to be not a defect, or human territory (a design");
        prompt.AppendLine("  disagreement, a scope change), do not paper over it and do not loop: state your");
        prompt.AppendLine("  position on that finding explicitly in your summary. The platform hands disputes");
        prompt.AppendLine("  to a human with both positions on record.");
        prompt.AppendLine();
        prompt.AppendLine("## Resolution (required)");
        prompt.AppendLine();
        prompt.AppendLine("End your final message with a summary of what you changed, then exactly one");
        prompt.AppendLine("resolution line, nothing after it:");
        prompt.AppendLine();
        prompt.AppendLine("    RESOLUTION: fixed");
        prompt.AppendLine();
        prompt.AppendLine("when every finding is resolved, or");
        prompt.AppendLine();
        prompt.AppendLine("    RESOLUTION: disputed");
        prompt.AppendLine();
        prompt.AppendLine("when any finding is, in your judgment, not a defect or a human decision.");

        return prompt.ToString();
    }

    /// <summary>
    /// The fan-in synthesis session (Decisions Log #36): when a claimed task has more
    /// immediate blockers than the node's threshold, this session condenses their handoffs
    /// into the one context document the build session actually reads. It is a platform
    /// dispatch like the reviewer — recorded model, recorded tokens, artifacts in the
    /// dependent run's own directory — and, like the reviewer, strictly read-only.
    /// <para>
    /// The instruction is to condense, never to judge: dropping a gotcha because it looked
    /// minor would defeat the whole point of routing it, and this session knows less about
    /// the dependent's work than the dependent will.
    /// </para>
    /// </summary>
    public static string BuildContextSynthesis(TaskDetails task, int blockerCount, string blockerContext)
    {
        StringBuilder prompt = new();
        prompt.AppendLine("# Condense these blocker handoffs into one starting context");
        prompt.AppendLine();
        prompt.AppendLine($"A task is about to start with the handoffs of {blockerCount} blockers it waited on.");
        prompt.AppendLine("That is a lot to open a session with, so your only job is to turn them into one");
        prompt.AppendLine("shorter document that the agent doing the work will read instead.");
        prompt.AppendLine();
        prompt.AppendLine("## The task that will read your output");
        prompt.AppendLine();
        prompt.AppendLine(task.Objective);
        prompt.AppendLine();
        if (task.AcceptanceCriteria.Count > 0)
        {
            prompt.AppendLine("Acceptance criteria:");
            foreach (string criterion in task.AcceptanceCriteria)
            {
                prompt.AppendLine($"- {criterion}");
            }

            prompt.AppendLine();
        }

        prompt.AppendLine("## The handoffs to condense");
        prompt.AppendLine();
        prompt.AppendLine(blockerContext);
        prompt.AppendLine();
        prompt.AppendLine("## How to condense");
        prompt.AppendLine();
        prompt.AppendLine("- Merge what overlaps and drop what repeats. Several blockers describing the same");
        prompt.AppendLine("  convention should leave one statement of it, not five.");
        prompt.AppendLine("- Keep every gotcha, constraint, and deliberate omission, even a small-looking one.");
        prompt.AppendLine("  You are shortening the text, not deciding what matters — the agent reading this");
        prompt.AppendLine("  knows its own work better than you do, and a dropped warning routes nothing.");
        prompt.AppendLine("- Keep each fact attached to the blocker it came from, so a claim can be traced.");
        prompt.AppendLine("- Say only what the handoffs say. Do not resolve contradictions between them by");
        prompt.AppendLine("  picking a side, and do not fill gaps from the code or from your own judgment:");
        prompt.AppendLine("  name the disagreement and move on. An invented fact here reads downstream as");
        prompt.AppendLine("  something a blocker actually reported.");
        prompt.AppendLine("- Do NOT modify files, commit, push, or open pull requests. You are read-only.");
        prompt.AppendLine();
        prompt.AppendLine("## Output");
        prompt.AppendLine();
        prompt.AppendLine("Your final message IS the document — it is pasted into the other agent's prompt");
        prompt.AppendLine($"verbatim, so write it for that reader. Open with the `{BlockerContextDocument.Heading}`");
        prompt.AppendLine("heading, keep the depth-one framing (these are immediate blockers; a fact needed from");
        prompt.AppendLine("two hops back means a missing dependency edge, not a gap to work around), and add no");
        prompt.AppendLine("preamble about having been asked to summarize.");

        return prompt.ToString();
    }

    private static IReadOnlyList<RepoSkill> DiscoverRepoSkills(string worktreePath)
    {
        string skillsDirectory = Path.Combine(worktreePath, ".claude", "skills");
        if (!Directory.Exists(skillsDirectory))
        {
            return [];
        }

        List<RepoSkill> skills = [];
        foreach (string skillDirectory in Directory.EnumerateDirectories(skillsDirectory).Order(StringComparer.Ordinal))
        {
            string manifestPath = Path.Combine(skillDirectory, "SKILL.md");
            if (File.Exists(manifestPath))
            {
                skills.Add(new RepoSkill(Path.GetFileName(skillDirectory), ReadFrontmatterDescription(manifestPath)));
            }
        }

        return skills;
    }

    private static string? ReadFrontmatterDescription(string manifestPath)
    {
        // Stream and stop at the frontmatter fence — the skill body below it can be large
        // and is never needed here.
        using IEnumerator<string> lines = File.ReadLines(manifestPath).GetEnumerator();
        if (!lines.MoveNext() || lines.Current.Trim() != "---")
        {
            return null;
        }

        while (lines.MoveNext())
        {
            string line = lines.Current;
            if (line.Trim() == "---")
            {
                break;
            }

            if (line.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
            {
                string description = line["description:".Length..].Trim();
                return description.IsNotBlank() ? description : null;
            }
        }

        return null;
    }

    private sealed record RepoSkill(string Name, string? Description);
}
