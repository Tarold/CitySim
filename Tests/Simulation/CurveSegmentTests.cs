using Godot;
using NUnit.Framework;
using CitySim.Simulation;

namespace CitySim.Tests.Simulation;

[TestFixture]
public class CurveSegmentTests
{
    [Test]
    public void StraightSegment_ControlPointsPlacedAtThirds()
    {
        Vector2 start = new Vector2(0, 0);
        Vector2 end = new Vector2(90, 0);

        var curve = new CurveSegment(start, end);

        Assert.That(curve.P0, Is.EqualTo(new Vector2(0, 0)));
        Assert.That(curve.P1, Is.EqualTo(new Vector2(30, 0)));
        Assert.That(curve.P2, Is.EqualTo(new Vector2(60, 0)));
        Assert.That(curve.P3, Is.EqualTo(new Vector2(90, 0)));
        Assert.That(curve.Length, Is.EqualTo(90f).Within(0.01f));
    }

    [Test]
    public void Evaluate_EndpointsAndMidpoint_CorrectPositions()
    {
        Vector2 p0 = new Vector2(0, 0);
        Vector2 p1 = new Vector2(0, 50);
        Vector2 p2 = new Vector2(100, 50);
        Vector2 p3 = new Vector2(100, 0);

        var curve = new CurveSegment(p0, p1, p2, p3);

        Vector2 at0 = curve.Evaluate(0.0f);
        Vector2 at1 = curve.Evaluate(1.0f);
        Vector2 atHalf = curve.Evaluate(0.5f);

        Assert.That(at0.DistanceTo(p0), Is.LessThan(0.001f));
        Assert.That(at1.DistanceTo(p3), Is.LessThan(0.001f));
        // Symmetry: midpoint X should be 50
        Assert.That(atHalf.X, Is.EqualTo(50f).Within(0.01f));
        Assert.That(atHalf.Y, Is.GreaterThan(0f));
    }

    [Test]
    public void CreateFromThreePoints_PassesExactlyThroughApex()
    {
        Vector2 start = new Vector2(0, 0);
        Vector2 apex = new Vector2(50, 40);
        Vector2 end = new Vector2(100, 0);

        var curve = CurveSegment.CreateFromThreePoints(start, apex, end);

        Vector2 evalApex = curve.Evaluate(0.5f);
        Assert.That(evalApex.DistanceTo(apex), Is.LessThan(0.01f));
    }

    [Test]
    public void TangentAndNormal_UnitLengthAndPerpendicular()
    {
        Vector2 start = new Vector2(0, 0);
        Vector2 apex = new Vector2(50, 30);
        Vector2 end = new Vector2(100, 0);

        var curve = CurveSegment.CreateFromThreePoints(start, apex, end);

        for (float t = 0f; t <= 1f; t += 0.2f)
        {
            Vector2 tangent = curve.GetTangent(t);
            Vector2 normal = curve.GetNormal(t);

            Assert.That(tangent.Length(), Is.EqualTo(1.0f).Within(0.001f));
            Assert.That(normal.Length(), Is.EqualTo(1.0f).Within(0.001f));
            Assert.That(tangent.Dot(normal), Is.EqualTo(0.0f).Within(0.001f));
        }
    }

    [Test]
    public void ArcLength_CurvedLongerThanChord()
    {
        Vector2 start = new Vector2(0, 0);
        Vector2 apex = new Vector2(50, 40);
        Vector2 end = new Vector2(100, 0);

        var straight = new CurveSegment(start, end);
        var curved = CurveSegment.CreateFromThreePoints(start, apex, end);

        Assert.That(straight.Length, Is.EqualTo(100f).Within(0.1f));
        Assert.That(curved.Length, Is.GreaterThan(straight.Length));
    }

    [Test]
    public void Reversal_SymmetricalAndReversedEndpoints()
    {
        Vector2 p0 = new Vector2(10, 20);
        Vector2 p1 = new Vector2(30, 80);
        Vector2 p2 = new Vector2(70, 90);
        Vector2 p3 = new Vector2(120, 30);

        var curve = new CurveSegment(p0, p1, p2, p3);
        var reversed = curve.GetReversed();

        Assert.That(reversed.P0, Is.EqualTo(curve.P3));
        Assert.That(reversed.P1, Is.EqualTo(curve.P2));
        Assert.That(reversed.P2, Is.EqualTo(curve.P1));
        Assert.That(reversed.P3, Is.EqualTo(curve.P0));
        Assert.That(reversed.Length, Is.EqualTo(curve.Length).Within(0.01f));

        Vector2 fwdMid = curve.Evaluate(0.5f);
        Vector2 revMid = reversed.Evaluate(0.5f);
        Assert.That(fwdMid.DistanceTo(revMid), Is.LessThan(0.01f));

        Vector2 fwdQuarter = curve.Evaluate(0.25f);
        Vector2 revThreeQuarter = reversed.Evaluate(0.75f);
        Assert.That(fwdQuarter.DistanceTo(revThreeQuarter), Is.LessThan(0.01f));
    }

    [Test]
    public void Split_DeCasteljau_PreservesGeometryAndLength()
    {
        Vector2 start = new Vector2(0, 0);
        Vector2 apex = new Vector2(60, 50);
        Vector2 end = new Vector2(120, 10);

        var curve = CurveSegment.CreateFromThreePoints(start, apex, end);
        float splitT = 0.4f;

        var (left, right) = curve.Split(splitT);

        // Junction position matches
        Vector2 splitPos = curve.Evaluate(splitT);
        Assert.That(left.P3.DistanceTo(splitPos), Is.LessThan(0.001f));
        Assert.That(right.P0.DistanceTo(splitPos), Is.LessThan(0.001f));

        // Subdivided arc lengths sum to original within 0.1%
        float sumLength = left.Length + right.Length;
        Assert.That(sumLength, Is.EqualTo(curve.Length).Within(curve.Length * 0.01f));

        // Point on left at t=0.5 should match original at t=0.2
        Vector2 leftEval = left.Evaluate(0.5f);
        Vector2 origEval = curve.Evaluate(splitT * 0.5f);
        Assert.That(leftEval.DistanceTo(origEval), Is.LessThan(0.05f));
    }

    [Test]
    public void GetClosestPoint_FindsAccurateProjection()
    {
        Vector2 start = new Vector2(0, 0);
        Vector2 apex = new Vector2(50, 40);
        Vector2 end = new Vector2(100, 0);

        var curve = CurveSegment.CreateFromThreePoints(start, apex, end);

        // Query directly on curve apex
        var (tApex, ptApex, distApex) = curve.GetClosestPoint(apex);
        Assert.That(tApex, Is.EqualTo(0.5f).Within(0.02f));
        Assert.That(distApex, Is.LessThan(0.5f));

        // Query slightly offset from midpoint
        Vector2 queryOffset = apex + new Vector2(0, 15);
        var (tOff, ptOff, distOff) = curve.GetClosestPoint(queryOffset);
        Assert.That(tOff, Is.EqualTo(0.5f).Within(0.05f));
        Assert.That(distOff, Is.EqualTo(15f).Within(1.0f));
    }

    [Test]
    public void MinimumRadiusOfCurvature_DetectsSharpAndGentleBends()
    {
        Vector2 start = new Vector2(0, 0);
        Vector2 gentleApex = new Vector2(50, 5);
        Vector2 sharpApex = new Vector2(50, 80);
        Vector2 end = new Vector2(100, 0);

        var gentle = CurveSegment.CreateFromThreePoints(start, gentleApex, end);
        var sharp = CurveSegment.CreateFromThreePoints(start, sharpApex, end);

        float gentleR = gentle.GetMinimumRadiusOfCurvature();
        float sharpR = sharp.GetMinimumRadiusOfCurvature();

        Assert.That(gentleR, Is.GreaterThan(sharpR));
        Assert.That(sharpR, Is.LessThan(50f));
    }
}
