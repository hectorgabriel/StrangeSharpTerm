using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.App.Views;
using StrangeSharpTerm.Assist;
using StrangeSharpTerm.Transport;

namespace StrangeSharpTerm.App.WindowTests;

/// <summary>
/// Saying in colour what the words already say.
///
/// This list exists so that a summary cannot read as though it covered hosts it
/// never reached, and a failed host was the same small grey word as one that
/// reported, in the same place. The same for the gate: it was red for every
/// command it stopped, and red for everything says nothing.
/// </summary>
[Collection("window")]
public class OutcomeStylingTests
{
    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static TextBlock Labelled(Visual root, string text) =>
        root.GetVisualDescendants().OfType<TextBlock>().First(block => block.Text == text);


    /// <summary>
    /// The gate says which kind of command it stopped. It used to be red for
    /// both, and a gate you stop reading is a gate that is not there.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void TheGateLooksLikeWhatTheCommandWouldDo(bool destructive, bool caution)
    {
        Headless.Run(() =>
        {
            var model = new OrchestratorViewModel(new Canned(Canned.Says("Summary.")), [], _ => null);
            var window = new Window { Content = new OrchestratorView(model), Width = 480, Height = 640 };
            window.Show();

            _ = model.Allow(new PendingCommand(
                "web-01", "systemctl restart nginx", "it is wedged", "It restarts a service.", destructive));
            Settle(window);

            var banner = window.GetVisualDescendants()
                .OfType<Border>()
                .First(border => border.Classes.Contains("banner") && border.IsVisible);

            banner.Classes.Contains("caution").ShouldBe(caution);

            // And the reason underneath is coloured the same way.
            var reason = Labelled(window, "It restarts a service.");
            reason.Classes.Contains("danger").ShouldBe(destructive);
            reason.Classes.Contains("warning").ShouldBe(caution);

            model.Dispose();
        });
    }

    private sealed class Quiet(string alias) : IHostAccess
    {
        public string Alias => alias;

        public Task<HostSnapshot> Look(bool metrics, bool tail, int lines, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HostSnapshot());

        public Task<CommandOutcome> Run(string command, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CommandOutcome(0, ""));
    }

    private sealed class Broken : IAssistBackend
    {
        public string ProviderName => "Broken";

        public string Model => "broken-1";

        public IAsyncEnumerable<AssistEvent> Stream(AssistRequest request, CancellationToken cancellationToken = default) =>
            throw new AssistException("No route to host.");
    }

    private sealed class Canned(params IReadOnlyList<AssistEvent>[] turns) : IAssistBackend
    {
        private int _turn;

        public string ProviderName => "Canned";

        public string Model => "canned-1";

        public static IReadOnlyList<AssistEvent> Says(string text) =>
            [new AssistEvent.Say(text), new AssistEvent.Finished(AssistStop.EndTurn)];

        public async IAsyncEnumerable<AssistEvent> Stream(
            AssistRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var turn = _turn < turns.Length ? turns[_turn] : Says("Nothing more.");
            _turn++;
            foreach (var streamed in turn)
            {
                await Task.Yield();
                yield return streamed;
            }
        }
    }
}
