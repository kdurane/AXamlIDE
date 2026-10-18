using AXamlIDE.Services.Problems;

namespace AXamlIDE.Tests;

public class ProblemNavigationServiceTests
{
    private sealed class Doc(string name)
    {
        public override string ToString() => name;
    }

    private static readonly Doc A = new("A"), B = new("B");
    private static readonly Doc[] Documents = [A, B];
    private static readonly (Doc, int)[] Problems = [(A, 10), (A, 50), (B, 5)];

    private readonly ProblemNavigationService _navigation = new();

    [Fact]
    public void Forward_goes_to_the_next_problem_after_the_caret()
        => Assert.Equal((A, 50), _navigation.FindNext(Problems, Documents, A, caretOffset: 20, forward: true));

    [Fact]
    public void Forward_moves_on_to_the_next_document()
        => Assert.Equal((B, 5), _navigation.FindNext(Problems, Documents, A, caretOffset: 60, forward: true));

    [Fact]
    public void Forward_wraps_around_to_the_first_problem()
        => Assert.Equal((A, 10), _navigation.FindNext(Problems, Documents, B, caretOffset: 100, forward: true));

    [Fact]
    public void Backward_goes_to_the_previous_problem_before_the_caret()
        => Assert.Equal((A, 10), _navigation.FindNext(Problems, Documents, A, caretOffset: 20, forward: false));

    [Fact]
    public void Backward_wraps_around_to_the_last_problem()
        => Assert.Equal((B, 5), _navigation.FindNext(Problems, Documents, A, caretOffset: 0, forward: false));

    [Fact]
    public void No_problems_gives_null()
        => Assert.Null(_navigation.FindNext<Doc>([], Documents, A, 0, forward: true));
}
