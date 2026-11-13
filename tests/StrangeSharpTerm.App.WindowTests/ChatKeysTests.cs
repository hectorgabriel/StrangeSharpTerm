using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.App.Views;
using StrangeSharpTerm.Assist;
using StrangeSharpTerm.Transport;

namespace StrangeSharpTerm.App.WindowTests;

/// <summary>
/// The chat's keys, pressed: delivered to the window the way a keyboard
/// delivers them, with the field focused, rather than asked of the view model.
/// </summary>
[Collection("window")]
public class ChatKeysTests
{
    private static AssistantViewModel Pane(params IReadOnlyList<AssistEvent>[] turns) =>
        new(
            new HostAgent(new Canned(turns), new Quiet("web-01"), new AssistSettings(), new StandingAnswer(true)),
            new AssistSettings(),
            _ => { });

    private static (Window, TextBox) Shown(AssistantViewModel pane)
    {
        var window = new Window { Width = 520, Height = 640, Content = new AssistantView(pane) };
        window.Show();
        Settle(window);
        var field = window.GetVisualDescendants().OfType<TextBox>().Last();
        field.Focus();
        Settle(window);
        return (window, field);
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    [Fact]
    public void ReturnSendsTheQuestion()
    {
        Headless.Run(() =>
        {
            var pane = Pane(Canned.Says("98% full."));
            var (window, _) = Shown(pane);

            window.KeyTextInput("how full is the disk?");
            Settle(window);
            pane.Question.ShouldBe("how full is the disk?");

            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "");
            Headless.Finish(pane.AskCommand.ExecutionTask ?? Task.CompletedTask);
            Settle(window);

            pane.Question.ShouldBe("");
            pane.Rows.ShouldNotBeEmpty();
        });
    }

    [Fact]
    public void ShiftReturnStartsANewLine()
    {
        Headless.Run(() =>
        {
            var pane = Pane(Canned.Says("98% full."));
            var (window, _) = Shown(pane);

            window.KeyTextInput("one");
            window.KeyPress(Key.Enter, RawInputModifiers.Shift, PhysicalKey.Enter, "");
            window.KeyTextInput("two");
            Settle(window);

            pane.Question.ReplaceLineEndings("\n").ShouldBe("one\ntwo");
            pane.Rows.ShouldBeEmpty();
        });
    }

    [Fact]
    public void UpBringsBackWhatWasAsked()
    {
        Headless.Run(() =>
        {
            var pane = Pane(Canned.Says("98% full."));
            var (window, _) = Shown(pane);

            pane.Question = "how full is the disk?";
            Headless.Finish(pane.AskCommand.ExecuteAsync(null));
            Settle(window);

            window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, "");
            Settle(window);

            pane.Question.ShouldBe("how full is the disk?");
        });
    }

    /// <summary>
    /// Up over half a question takes it away, and Down past the newest thing
    /// asked puts it back -- which is what makes Up safe to press.
    /// </summary>
    [Fact]
    public void WhatWasBeingWrittenComesBackAfterAWalk()
    {
        Headless.Run(() =>
        {
            var pane = Pane(Canned.Says("98% full."));
            var (window, _) = Shown(pane);

            pane.Question = "how full is the disk?";
            Headless.Finish(pane.AskCommand.ExecuteAsync(null));
            Settle(window);

            window.KeyTextInput("and the jour");
            window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, "");
            Settle(window);
            pane.Question.ShouldBe("how full is the disk?");

            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, "");
            Settle(window);
            pane.Question.ShouldBe("and the jour");
        });
    }

    /// <summary>Inside a question of several lines, Up moves the caret and recalls nothing.</summary>
    [Fact]
    public void UpInsideALongQuestionOnlyMovesTheCaret()
    {
        Headless.Run(() =>
        {
            var pane = Pane(Canned.Says("98% full."));
            var (window, field) = Shown(pane);

            pane.Question = "how full is the disk?";
            Headless.Finish(pane.AskCommand.ExecuteAsync(null));
            Settle(window);

            pane.Question = "one\ntwo";
            Settle(window);
            field.CaretIndex = field.Text!.Length;
            window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, "");
            Settle(window);

            pane.Question.ShouldBe("one\ntwo");
        });
    }

    /// <summary>The fan-out pane takes Return and Up the same way.</summary>
    [Fact]
    public void TheOrchestratorTakesTheSameKeys()
    {
        Headless.Run(() =>
        {
            var model = new OrchestratorViewModel(
                new Canned(Canned.Says("All fine.")),
                [new TargetRow { Alias = "web-01", IsConnected = true, IsChosen = true }],
                _ => null);
            var window = new Window { Width = 720, Height = 700, Content = new OrchestratorView(model) };
            window.Show();
            Settle(window);
            var field = window.GetVisualDescendants().OfType<TextBox>()
                .First(box => box.Classes.Contains("chat") && box.IsEffectivelyVisible);
            field.Focus();
            Settle(window);

            window.KeyTextInput("how are they?");
            Settle(window);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "");
            Headless.Finish(model.RunCommand.ExecutionTask ?? Task.CompletedTask);
            Settle(window);

            model.Instruction.ShouldBe("");
            model.Asked.ShouldBe("how are they?");

            window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, "");
            Settle(window);
            model.Instruction.ShouldBe("how are they?");
        });
    }
}
