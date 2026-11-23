using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Cerebral.Desktop;

public sealed partial class WorkflowWorkspace
{
    /// <summary>Host navigation must not discard an unfinished answer or an uncertain answer request.</summary>
    public bool HasUnsubmittedQuestionDraft
    {
        get
        {
            Dispatcher.VerifyAccess();
            if (pendingAnswer is not null) return true;
            var questions = Snapshot?.Run?.Questions;
            if (questions is null) return false;
            return questions.Fields.Any(question => !questions.Answers.ContainsKey(question.Id) &&
                answers.TryGetValue(question.Id, out var read) && HasAnswer(question, read()));
        }
    }

    private static bool HasAnswer(Cerebral.Contracts.Workflows.WorkflowQuestion question, string value) =>
        !string.IsNullOrWhiteSpace(value) && (!question.Multiple || value.Trim() != "[]");

    /// <summary>Explicit user activation only. Reveals an already selected exact run; never answers or submits.</summary>
    public bool RevealAttentionTarget(string runId, long minimumRunRevision, string? questionId,
        long? questionRevision, string? questionFieldId)
    {
        Dispatcher.VerifyAccess();
        if (disposed || minimumRunRevision < 1 || Snapshot?.Run is not { } run || run.Id != runId ||
            Snapshot.RunId != runId || run.Revision < minimumRunRevision) return false;
        FrameworkElement? target = null;
        if (questionId is not null)
        {
            var questions = run.Questions;
            if (questions is null || questions.Id != questionId || questions.Revision != questionRevision ||
                questionFieldId is null || questions.Answers.ContainsKey(questionFieldId) ||
                !questions.Fields.Any(question => question.Id == questionFieldId)) return false;
            target = fields.Children.OfType<FrameworkElement>().FirstOrDefault(control =>
                AutomationProperties.GetAutomationId(control) == "WorkflowAnswer-" + questionFieldId);
            if (target is null) return false;
        }
        else if (!run.IsTerminal) return false;

        // This explicit navigation replaces only reader position. It does not
        // rewrite the composer, answer controls, attachment drafts or pending RID.
        transcriptInputRevision++;
        pendingScrollRestore = null; pendingQuestionFocus = null; transcriptRestoring = false;
        readingHistory = false;
        UpdateLayout();
        if (target is not null)
        {
            answerHost.BringIntoView();
            target.BringIntoView();
            if (target.Focusable) target.Focus();
            else if (target is Panel panel) panel.Children.OfType<Control>().FirstOrDefault(control => control.Focusable)?.Focus();
        }
        else transcriptScroller.ScrollToEnd();
        ObserveReadViewport();
        return true;
    }
}
