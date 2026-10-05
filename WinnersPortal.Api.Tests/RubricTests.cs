using WinnersPortal.Services.Opportunities;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The terms beside the brief: the requirements table, the scoring rubric
/// and the milestone shares. Pure rules, no database.
/// </summary>
public class RubricTests
{
    [Fact]
    public void Requirements_are_tidied_and_blank_rows_dropped()
    {
        var rows = Rubric.CleanRequirements(
        [
            new("  Language ", " Python   3.11+ "),
            new("", ""),
            new(null, null),
            new("Frameworks", "PyTorch / TensorFlow"),
        ], out var problem);

        Assert.Null(problem);
        Assert.Equal([("Language", "Python 3.11+"), ("Frameworks", "PyTorch / TensorFlow")], rows);
    }

    [Fact]
    public void A_half_filled_requirement_is_refused_with_the_row_named()
    {
        Assert.Null(Rubric.CleanRequirements([new("Language", "")], out var noDetail));
        Assert.Contains("name and what it asks for", noDetail);
        Assert.Null(Rubric.CleanRequirements([new("", "Python")], out var noName));
        Assert.Contains("name and what it asks for", noName);
    }

    [Fact]
    public void Requirements_have_a_ceiling()
    {
        var many = Enumerable.Range(0, Rubric.MaxRequirements + 1)
            .Select(i => new RequirementInput($"R{i}", "x")).ToList();
        Assert.Null(Rubric.CleanRequirements(many, out var problem));
        Assert.Contains($"At most {Rubric.MaxRequirements}", problem);
    }

    [Fact]
    public void Criteria_keep_their_points_and_drop_blank_rows()
    {
        var rows = Rubric.CleanCriteria(
        [
            new(35, "Functionality", "All requirements met and working end-to-end"),
            new(null, "", ""),
            new(10, "Documentation", null),
        ], out var problem);

        Assert.Null(problem);
        Assert.Equal(2, rows!.Count);
        Assert.Equal((35, "Functionality", "All requirements met and working end-to-end"), rows[0]);
        Assert.Equal((10, "Documentation", (string?)null), rows[1]);
        Assert.Equal(45, rows.Sum(r => r.Points));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(101)]
    public void A_criterion_needs_points_on_the_scale(int? points)
    {
        Assert.Null(Rubric.CleanCriteria([new(points, "Craft", null)], out var problem));
        Assert.Contains("“Craft”", problem);
    }

    [Fact]
    public void Points_without_a_name_are_refused()
    {
        Assert.Null(Rubric.CleanCriteria([new(20, " ", "judged by eye")], out var problem));
        Assert.Contains("needs a name", problem);
    }

    [Fact]
    public void A_draft_accepts_blank_shares_and_refuses_impossible_ones()
    {
        Assert.Null(Rubric.WeightProblem([null, 25, null]));
        Assert.NotNull(Rubric.WeightProblem([0]));
        Assert.NotNull(Rubric.WeightProblem([101]));
    }

    [Fact]
    public void Publish_wants_every_share_or_none_and_the_whole()
    {
        Assert.Null(Rubric.WeightPublishProblem([null, null, null]));
        Assert.Null(Rubric.WeightPublishProblem([25, 25, 50]));
        Assert.Contains("every milestone", Rubric.WeightPublishProblem([25, null, 75]));
        Assert.Contains("90%", Rubric.WeightPublishProblem([40, 50]));
    }
}
