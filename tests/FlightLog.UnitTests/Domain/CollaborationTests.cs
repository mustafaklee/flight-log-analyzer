using FlightLog.Domain.Analysis;
using FlightLog.Domain.Collaboration;
using FlightLog.Domain.Common;
using FlightLog.Domain.Flights;
using FlightLog.UnitTests.Ai;

namespace FlightLog.UnitTests.Domain;

public sealed class CollaborationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TeamId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly string[] MixedCaseTags = ["acceptance", " Acceptance ", "", "GPS test"];
    private static readonly string[] ReorderedTags = ["GPS test", "acceptance"];

    [Fact]
    public void A_review_is_keyed_by_the_findings_rule_and_time_range()
    {
        var finding = ContextFixtures.Finding(Guid.NewGuid(), "gps.hdop", FindingSeverity.Warning, 10, 20, [], []);
        var review = FindingReview.Start(TeamId, finding, UserId, Now);

        review.Status.Should().Be(FindingReviewStatus.Open);
        review.Matches(finding).Should().BeTrue();
        var reanalyzed = ContextFixtures.Finding(Guid.NewGuid(), "gps.hdop", FindingSeverity.Critical, 10, 20, [], []);
        review.Matches(reanalyzed).Should().BeTrue("a new analysis reports the same finding with a new id");
        review.Matches(ContextFixtures.Finding(Guid.NewGuid(), "gps.hdop", FindingSeverity.Warning, 10, 21, [], [])).Should().BeFalse();
        review.Matches(ContextFixtures.Finding(Guid.NewGuid(), "gps.satellites", FindingSeverity.Warning, 10, 20, [], [])).Should().BeFalse();
    }

    [Fact]
    public void Review_changes_report_whether_anything_changed()
    {
        var review = FindingReview.Start(TeamId, ContextFixtures.Finding(Guid.NewGuid(), "data.gaps", FindingSeverity.Info, 1, 2, [], []), UserId, Now);
        var other = Guid.NewGuid();
        var later = Now.AddMinutes(5);

        review.ChangeStatus(FindingReviewStatus.Open, other, later).Should().BeFalse();
        review.UpdatedAt.Should().Be(Now);
        review.ChangeStatus(FindingReviewStatus.Confirmed, other, later).Should().BeTrue();
        review.UpdatedBy.Should().Be(other);
        review.UpdatedAt.Should().Be(later);

        review.Assign(other, other, later).Should().BeTrue();
        review.Assign(other, other, later).Should().BeFalse();
        review.Assign(Guid.Empty, other, later).Should().BeTrue("an empty id clears the assignee");
        review.AssigneeId.Should().BeNull();

        review.SetFlag(true, other, later).Should().BeTrue();
        review.SetFlag(true, other, later).Should().BeFalse();

        var invalid = () => review.ChangeStatus((FindingReviewStatus)99, other, later);
        invalid.Should().Throw<DomainRuleViolationException>().Which.Code.Should().Be("review.invalid_status");
    }

    [Fact]
    public void A_comment_can_mark_a_point_or_a_range_of_the_flight()
    {
        var flightId = Guid.NewGuid();
        FlightComment.Create(TeamId, flightId, null, UserId, "general", null, null, Now).StartUs.Should().BeNull();
        var point = FlightComment.Create(TeamId, flightId, null, UserId, "here", 5_000, null, Now);
        (point.StartUs, point.EndUs).Should().Be((5_000L, 5_000L));
        var range = FlightComment.Create(TeamId, flightId, Guid.Empty, UserId, " range ", 5_000, 9_000, Now);
        range.Body.Should().Be("range");
        range.FindingReviewId.Should().BeNull();

        var backwards = () => FlightComment.Create(TeamId, flightId, null, UserId, "x", 9_000, 5_000, Now);
        backwards.Should().Throw<DomainRuleViolationException>().Which.Code.Should().Be("comment.invalid_range");
        var negative = () => FlightComment.Create(TeamId, flightId, null, UserId, "x", -1, null, Now);
        negative.Should().Throw<DomainRuleViolationException>().Which.Code.Should().Be("comment.invalid_range");
        var empty = () => FlightComment.Create(TeamId, flightId, null, UserId, "  ", null, null, Now);
        empty.Should().Throw<DomainRuleViolationException>();
        var tooLong = () => FlightComment.Create(TeamId, flightId, null, UserId, new string('x', FlightComment.BodyMaxLength + 1), null, null, Now);
        tooLong.Should().Throw<DomainRuleViolationException>();
    }

    [Fact]
    public void A_deleted_comment_keeps_who_deleted_it_and_cannot_be_edited()
    {
        var comment = FlightComment.Create(TeamId, Guid.NewGuid(), null, UserId, "first", null, null, Now);
        comment.Edit("second", Now.AddMinutes(1));
        comment.Body.Should().Be("second");
        comment.EditedAt.Should().Be(Now.AddMinutes(1));

        var manager = Guid.NewGuid();
        comment.Delete(manager, Now.AddMinutes(2));
        comment.IsDeleted.Should().BeTrue();
        comment.Body.Should().BeNull();
        comment.DeletedBy.Should().Be(manager);

        var edit = () => comment.Edit("third", Now);
        edit.Should().Throw<DomainRuleViolationException>().Which.Code.Should().Be("comment.deleted");
        var delete = () => comment.Delete(manager, Now);
        delete.Should().Throw<DomainRuleViolationException>().Which.Code.Should().Be("comment.deleted");
    }

    [Fact]
    public void Flight_tags_are_trimmed_deduplicated_and_bounded()
    {
        var flight = Flight.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, "f", Autopilot.Px4, UserId, Now);
        flight.Tags.Should().BeEmpty();

        flight.SetTags(MixedCaseTags, Now.AddMinutes(1)).Should().BeTrue();
        flight.Tags.Should().Equal("acceptance", "GPS test");
        flight.UpdatedAt.Should().Be(Now.AddMinutes(1));
        flight.SetTags(MixedCaseTags, Now.AddMinutes(2)).Should().BeFalse();
        flight.SetTags(ReorderedTags, Now.AddMinutes(3)).Should().BeTrue("the order is the user's");
        flight.SetTags(null, Now).Should().BeTrue();
        flight.Tags.Should().BeEmpty();

        var tooLong = () => flight.SetTags([new string('x', Tags.MaxLength + 1)], Now);
        tooLong.Should().Throw<DomainRuleViolationException>().Which.Code.Should().Be("tags.too_long");
        var tooMany = () => flight.SetTags(Enumerable.Range(0, Tags.MaxCount + 1).Select(i => $"t{i}"), Now);
        tooMany.Should().Throw<DomainRuleViolationException>().Which.Code.Should().Be("tags.too_many");
        flight.SetTags(Enumerable.Range(0, Tags.MaxCount).Select(i => $"T{i}").Concat(["t0"]), Now).Should().BeTrue("duplicates do not count");
    }
}
