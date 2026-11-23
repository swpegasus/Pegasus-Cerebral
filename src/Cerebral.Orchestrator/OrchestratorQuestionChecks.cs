using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Cerebral.Core;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    // Deterministic lifecycle acceptance; no service, model, plugin, microphone or user data is accessed.
    public int CheckQuestionUi()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
        static bool ContainsAutomationId(DependencyObject parent, string id)
        {
            if (parent is FrameworkElement element && AutomationProperties.GetAutomationId(element) == id) return true;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
                if (ContainsAutomationId(VisualTreeHelper.GetChild(parent, index), id)) return true;
            return false;
        }
        static T? FindAutomationId<T>(DependencyObject parent, string id) where T : FrameworkElement
        {
            if (parent is T element && AutomationProperties.GetAutomationId(element) == id) return element;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
                if (FindAutomationId<T>(VisualTreeHelper.GetChild(parent, index), id) is { } match) return match;
            return null;
        }
        static Rect Bounds(FrameworkElement element, FrameworkElement ancestor)
        {
            var origin = element.TransformToAncestor(ancestor).Transform(new Point());
            return new Rect(origin, new Size(element.ActualWidth, element.ActualHeight));
        }

        try
        {
            var now = DateTimeOffset.UtcNow;
            session = new TaskSession
            {
                Id = "question-render-fixture", ProfileId = "lead", Title = "Question lifecycle",
                Messages =
                [
                    new() { Sequence = 1, Role = "user", Origin = "user", Content = "Help choose a project format.", At = now.AddMinutes(-2) },
                    new() { Sequence = 2, Role = "assistant", Origin = "user", Content = "Question: Which format should this project use?", At = now.AddMinutes(-1) },
                    new() { Sequence = 3, Role = "user", Origin = "user", Content = "Answer to: Which format should this project use?\nA console application", At = now },
                    new() { Sequence = 4, Role = "user", Origin = "question-answer", SourceQuestionId = "answered-helper",
                        Content = "Question from Coding helper:\nWhich color should the cover use?\n\nYour answer:\nUse blue", At = now.AddSeconds(1) }
                ]
            };
            var answered = new UserQuestion
            {
                Id = "answered", SessionId = session.Id, JobId = "job", Prompt = "Which format should this project use?",
                Required = true, Answer = "A console application", CreatedAt = now.AddMinutes(-1)
            };
            var pending = new UserQuestion
            {
                Id = "pending", SessionId = session.Id, JobId = "job", Prompt = "Which output folder should be used?",
                Reason = "The destination changes the saved result.", Required = true, CreatedAt = now
            };
            var pendingSecond = new UserQuestion
            {
                Id = "pending-second", SessionId = session.Id, JobId = "job", Prompt = "Which public name should the project use?",
                Reason = "The answer controls the next public identifier.", Required = true, CreatedAt = now.AddMilliseconds(1)
            };
            activeJobs = [new AgentJob { Id = "job", SessionId = session.Id, ProfileId = "lead", Status = "waiting-human" }];
            answerDrafts[pending.Id] = "Use the configured project root";
            answerDrafts[pendingSecond.Id] = "Pegasus Sample";

            RenderQuestions(session.Id, [answered, pendingSecond, pending]);
            Check(questionPanel.Children.Count == 2 && loadedQuestionCount == 2 &&
                    ContainsAutomationId(questionPanel, "QuestionCard-pending") && ContainsAutomationId(questionPanel, "QuestionCard-pending-second"),
                "Every unresolved question has its own editable form in stable creation order.");
            Check(!ContainsAutomationId(questionPanel, "QuestionCard-answered") &&
                    ContainsAutomationId(questionPanel, "SubmitAnswer-pending") && ContainsAutomationId(questionPanel, "SubmitAnswer-pending-second") &&
                    ContainsAutomationId(questionPanel, "QuestionAnswer-pending") && ContainsAutomationId(questionPanel, "QuestionAnswer-pending-second"),
                "A saved answer removes its form while each pending question retains a separate answer and submit action.");
            Check(FindAutomationId<TextBox>(questionPanel, "QuestionAnswer-pending")?.Text == "Use the configured project root" &&
                    FindAutomationId<TextBox>(questionPanel, "QuestionAnswer-pending-second")?.Text == "Pegasus Sample",
                "Each simultaneous question preserves its own response draft.");
            Check(messages.Children.Count > 1 && session.Messages.Count(x => x.Content.StartsWith("Answer to:", StringComparison.Ordinal)) == 1,
                "The retained user answer remains in the ordinary conversation instead of being duplicated as an answered form.");
            Check(ContainsAutomationId(messages, "QuestionAnswer-answered-helper"),
                "A delegated answer appears as one compact receipt in the lead conversation after its form collapses.");
            var container = (FrameworkElement)Content;
            container.Measure(new Size(720, 516)); container.Arrange(new Rect(0, 0, 720, 516)); container.UpdateLayout();
            UpdateConversationLayout(); container.UpdateLayout();
            var host = questionHost ?? throw new InvalidOperationException("The pinned question host was not constructed.");
            var center = conversationCenter ?? throw new InvalidOperationException("The conversation center was not constructed.");
            var composer = conversationComposer ?? throw new InvalidOperationException("The conversation composer was not constructed.");
            var transcriptBounds = Bounds(transcript, center);
            Check(host.Visibility == Visibility.Visible && transcript.Content is StackPanel flow &&
                ReferenceEquals(host.Parent, flow) && flow.Children.Contains(messages) && !messages.Children.Contains(questionPanel),
                "Unresolved questions share the conversation page flow instead of an independent composer box.");
            Check(host.ActualHeight > 280 && double.IsPositiveInfinity(host.MaxHeight) &&
                !DescendantScrollViewer(host) && transcriptBounds.Height > 0 && composer.Visibility == Visibility.Collapsed,
                "The full question set grows naturally in the primary page scroll without a fixed inner viewport.");

            pending.Answer = "Use the configured project root";
            answerDrafts.Remove(pending.Id);
            session.Messages.Add(new() { Sequence = 5, Role = "user", Origin = "question-answer", SourceQuestionId = pending.Id,
                Content = "Answer to: Which output folder should be used?\nUse the configured project root", At = now.AddSeconds(2) });
            RenderQuestions(session.Id, [answered, pending, pendingSecond]);
            Check(questionPanel.Children.Count == 1 && loadedQuestionCount == 1 &&
                    !ContainsAutomationId(questionPanel, "QuestionCard-pending") && ContainsAutomationId(questionPanel, "QuestionCard-pending-second"),
                "Answering one question collapses only that question and preserves the other response form.");
            Check(ContainsAutomationId(messages, "QuestionAnswer-pending") &&
                    FindAutomationId<TextBox>(questionPanel, "QuestionAnswer-pending-second")?.Text == "Pegasus Sample",
                "The saved answer immediately replaces its form in the transcript without disturbing the remaining draft.");

            pendingSecond.Answer = "Pegasus Sample";
            answerDrafts.Remove(pendingSecond.Id);
            session.Messages.Add(new() { Sequence = 6, Role = "user", Origin = "question-answer", SourceQuestionId = pendingSecond.Id,
                Content = "Answer to: Which public name should the project use?\nPegasus Sample", At = now.AddSeconds(3) });
            RenderQuestions(session.Id, [answered, pending, pendingSecond]);
            Check(questionPanel.Children.Count == 0 && loadedQuestionCount == 0 && !ContainsAutomationId(questionPanel, "SubmitAnswer-pending"),
                "Submitting the last answer collapses the question surface completely.");
            Check(host.Visibility == Visibility.Collapsed && composer.Visibility == Visibility.Visible,
                "Resolving the last question must restore the same ordinary composer immediately.");
            Check(QuestionsRequiringInput([answered, pending, pendingSecond]).Count == 0,
                "Answered questions never return to the editable queue on refresh.");

            var abandoned = new UserQuestion { Id = "abandoned", SessionId = session.Id, JobId = "stopped", Prompt = "Obsolete required question", Required = true };
            Check(QuestionsRequiringInput([abandoned]).Count == 0,
                "A required question from terminal or absent work does not leave a dead answer form in the conversation.");

            container.Measure(new Size(1280, 840)); container.Arrange(new Rect(0, 0, 1280, 840)); container.UpdateLayout();
            Check(container.ActualWidth == 1280 && container.ActualHeight == 840,
                "The answered conversation lays out in the real WPF shell.");
            Console.WriteLine(JsonSerializer.Serialize(new { success = true, questionChecks = checks, pendingForms = questionPanel.Children.Count,
                serviceCommandsExecuted = false, modelInvoked = false, dictationInvoked = false, windowShown = false }));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { success = false, error = ex.ToString(), questionChecks = checks }));
            return 1;
        }
        finally { Close(); }
    }

    private static bool DescendantScrollViewer(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is System.Windows.Controls.Primitives.TextBoxBase) continue;
            if (child is ScrollViewer || DescendantScrollViewer(child)) return true;
        }
        return false;
    }
}
