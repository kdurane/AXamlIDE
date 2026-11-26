using AXamlIDE.Services.Navigation;

namespace AXamlIDE.Tests;

public class NavigationHistoryTests
{
    private static NavigationPoint At(string file, int offset) => new(file, offset);

    [Fact]
    public void Starts_with_nowhere_to_go()
    {
        var history = new NavigationHistory();
        Assert.False(history.CanGoBack);
        Assert.False(history.CanGoForward);
        Assert.Null(history.Back());
        Assert.Null(history.Forward());
    }

    [Fact]
    public void Back_returns_to_where_the_jump_came_from_and_forward_to_where_it_went()
    {
        var history = new NavigationHistory();
        history.Record(At("a.cs", 10));
        history.Record(At("b.cs", 50));

        Assert.Equal(At("a.cs", 10), history.Back());
        Assert.False(history.CanGoBack);
        Assert.Equal(At("b.cs", 50), history.Forward());
        Assert.False(history.CanGoForward);
    }

    [Fact]
    public void A_new_jump_after_going_back_drops_the_forward_points()
    {
        var history = new NavigationHistory();
        history.Record(At("a.cs", 10));
        history.Record(At("b.cs", 50));
        history.Back();
        history.Record(At("c.cs", 1));

        Assert.False(history.CanGoForward);
        Assert.Equal(At("a.cs", 10), history.Back());
    }

    [Fact]
    public void The_same_place_is_not_recorded_twice()
    {
        var history = new NavigationHistory();
        history.Record(At("a.cs", 10));
        history.Record(At("a.cs", 10));
        Assert.Single(history.Points);
    }

    [Fact]
    public void A_nearby_place_updates_the_current_point_instead_of_adding_one()
    {
        var history = new NavigationHistory();
        history.Record(At("a.cs", 10));
        history.Record(At("a.cs", 14), sameLineTolerance: 5);
        Assert.Single(history.Points);
        Assert.Equal(14, history.Points[0].Offset);
    }

    [Fact]
    public void Moves_made_while_navigating_are_not_recorded()
    {
        var history = new NavigationHistory();
        history.Record(At("a.cs", 10));
        history.Record(At("b.cs", 50));
        history.Navigating(() => history.Record(At("a.cs", 10)));
        Assert.Equal(2, history.Points.Count);
    }

    [Fact]
    public void Forgetting_a_file_removes_its_points_and_keeps_the_position_sane()
    {
        var history = new NavigationHistory(System.StringComparer.OrdinalIgnoreCase);
        history.Record(At("a.cs", 10));
        history.Record(At("B.cs", 50));
        history.Record(At("c.cs", 5));
        history.Forget("b.cs");

        Assert.Equal([At("a.cs", 10), At("c.cs", 5)], history.Points);
        Assert.Equal(At("a.cs", 10), history.Back());
    }

    [Fact]
    public void The_back_list_is_nearest_first_and_a_jump_into_it_leaves_forward_through_the_skipped()
    {
        var history = new NavigationHistory();
        var changes = 0;
        history.Changed += () => changes++;
        history.Record(At("a.cs", 1));
        history.Record(At("b.cs", 2));
        history.Record(At("c.cs", 3));
        history.Record(At("d.cs", 4));
        Assert.Equal(4, changes);

        Assert.Equal([At("c.cs", 3), At("b.cs", 2)], history.BackPoints(2));
        Assert.Equal(At("b.cs", 2), history.Back(2));
        Assert.Equal([At("a.cs", 1)], history.BackPoints(10));
        Assert.Equal(At("c.cs", 3), history.Forward());
        Assert.Null(history.Back(5));
        Assert.Equal(6, changes);
    }
}
