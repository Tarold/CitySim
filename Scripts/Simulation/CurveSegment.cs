using Godot;
using System;
using System.Collections.Generic;

namespace CitySim.Simulation;

/// <summary>
/// Represents a cubic Bézier curve segment in 2D space used for continuous road geometry.
/// Provides evaluation, tangent, normal, arc length precomputation, curve subdivision,
/// and closest-point projection.
/// </summary>
public class CurveSegment
{
    private readonly float[] _cumulativeLengths;
    private readonly Vector2[] _sampledPoints;
    private const int DefaultSampleCount = 32;

    /// <summary>Start point P0 of the cubic Bézier curve.</summary>
    public Vector2 P0 { get; }

    /// <summary>First control point P1.</summary>
    public Vector2 P1 { get; }

    /// <summary>Second control point P2.</summary>
    public Vector2 P2 { get; }

    /// <summary>End point P3.</summary>
    public Vector2 P3 { get; }

    /// <summary>The true precomputed arc length of the curve.</summary>
    public float Length { get; }

    /// <summary>
    /// Initializes a cubic Bézier curve from four control points.
    /// Precomputes arc length and samples using polyline discretization.
    /// </summary>
    public CurveSegment(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, int sampleCount = DefaultSampleCount)
    {
        P0 = p0;
        P1 = p1;
        P2 = p2;
        P3 = p3;

        int samples = Math.Max(sampleCount, 8);
        _sampledPoints = new Vector2[samples + 1];
        _cumulativeLengths = new float[samples + 1];

        _sampledPoints[0] = P0;
        _cumulativeLengths[0] = 0f;

        float totalLength = 0f;
        for (int i = 1; i <= samples; i++)
        {
            float t = (float)i / samples;
            _sampledPoints[i] = EvaluateRaw(t);
            totalLength += _sampledPoints[i - 1].DistanceTo(_sampledPoints[i]);
            _cumulativeLengths[i] = totalLength;
        }

        Length = totalLength;
    }

    /// <summary>
    /// Returns true if this curve deviates significantly from a straight line.
    /// </summary>
    public bool IsCurved => Length > P0.DistanceTo(P3) + 0.1f;

    /// <summary>
    /// Constructs a straight road curve segment between two endpoints.
    /// Control points P1 and P2 are placed along the straight line at 1/3 and 2/3 of the distance.
    /// </summary>
    public CurveSegment(Vector2 p0, Vector2 p3)
        : this(p0, p0 + (p3 - p0) / 3.0f, p0 + 2.0f * (p3 - p0) / 3.0f, p3)
    {
    }

    /// <summary>
    /// Factory method to construct a cubic Bézier curve from three points:
    /// start, arc apex (bend point), and end point.
    /// When evaluated at t = 0.5, the curve passes exactly through <paramref name="apex"/>.
    /// </summary>
    public static CurveSegment CreateFromThreePoints(Vector2 start, Vector2 apex, Vector2 end)
    {
        // For a quadratic Bézier Q(0.5) = 0.25*P0 + 0.5*Pc + 0.25*P3 = apex
        // => Pc = 2*apex - 0.5*(start + end)
        Vector2 controlQuad = 2.0f * apex - 0.5f * (start + end);

        // Degree elevation from quadratic to cubic Bézier:
        // P1 = P0 + 2/3*(Pc - P0)
        // P2 = P3 + 2/3*(Pc - P3)
        Vector2 p1 = start + (2.0f / 3.0f) * (controlQuad - start);
        Vector2 p2 = end + (2.0f / 3.0f) * (controlQuad - end);

        return new CurveSegment(start, p1, p2, end);
    }

    /// <summary>
    /// Evaluates the 2D world position at parameter <paramref name="t"/> in [0, 1].
    /// </summary>
    public Vector2 Evaluate(float t)
    {
        t = Mathf.Clamp(t, 0.0f, 1.0f);
        return EvaluateRaw(t);
    }

    private Vector2 EvaluateRaw(float t)
    {
        float u = 1.0f - t;
        float tt = t * t;
        float uu = u * u;
        float uuu = uu * u;
        float ttt = tt * t;

        return uuu * P0 +
               3.0f * uu * t * P1 +
               3.0f * u * tt * P2 +
               ttt * P3;
    }

    /// <summary>
    /// Computes the normalized first derivative (tangent vector) at parameter <paramref name="t"/>.
    /// Falls back safely if the derivative vector magnitude is near zero.
    /// </summary>
    public Vector2 GetTangent(float t)
    {
        t = Mathf.Clamp(t, 0.0f, 1.0f);
        float u = 1.0f - t;

        // B'(t) = 3*(1-t)^2*(P1-P0) + 6*(1-t)*t*(P2-P1) + 3*t^2*(P3-P2)
        Vector2 d = 3.0f * u * u * (P1 - P0) +
                    6.0f * u * t * (P2 - P1) +
                    3.0f * t * t * (P3 - P2);

        float len = d.Length();
        if (len > 1e-5f)
        {
            return d / len;
        }

        // Degenerate derivative fallback
        Vector2 fallback = P3 - P0;
        if (fallback.LengthSquared() > 1e-6f)
            return fallback.Normalized();

        return Vector2.Right;
    }

    /// <summary>
    /// Computes the perpendicular normal vector at parameter <paramref name="t"/>
    /// for lane and sidewalk lateral offsets.
    /// </summary>
    public Vector2 GetNormal(float t)
    {
        Vector2 tangent = GetTangent(t);
        return new Vector2(-tangent.Y, tangent.X);
    }

    /// <summary>
    /// Produces a reversed curve running from P3 to P0 for the opposing traffic direction.
    /// </summary>
    public CurveSegment GetReversed()
    {
        return new CurveSegment(P3, P2, P1, P0, _sampledPoints.Length - 1);
    }

    /// <summary>
    /// Subdivides this cubic Bézier curve at parameter <paramref name="t"/> into two
    /// consecutive cubic Bézier segments using De Casteljau's algorithm.
    /// </summary>
    public (CurveSegment Left, CurveSegment Right) Split(float t)
    {
        t = Mathf.Clamp(t, 0.0f, 1.0f);

        Vector2 p01 = P0.Lerp(P1, t);
        Vector2 p12 = P1.Lerp(P2, t);
        Vector2 p23 = P2.Lerp(P3, t);

        Vector2 p012 = p01.Lerp(p12, t);
        Vector2 p123 = p12.Lerp(p23, t);

        Vector2 p0123 = p012.Lerp(p123, t);

        var left = new CurveSegment(P0, p01, p012, p0123);
        var right = new CurveSegment(p0123, p123, p23, P3);

        return (left, right);
    }

    /// <summary>
    /// Finds the closest point on the curve to the specified query point.
    /// Returns the parameter t, the 2D world position, and the minimum distance.
    /// </summary>
    public (float T, Vector2 Point, float Distance) GetClosestPoint(Vector2 queryPoint, int refinementSteps = 5)
    {
        int sampleCount = _sampledPoints.Length - 1;
        float bestDistSq = float.MaxValue;
        int bestIdx = 0;
        float bestT = 0f;

        for (int i = 0; i < sampleCount; i++)
        {
            Vector2 a = _sampledPoints[i];
            Vector2 b = _sampledPoints[i + 1];
            Vector2 ab = b - a;
            float abLenSq = ab.LengthSquared();

            float segT = 0f;
            if (abLenSq > 1e-6f)
            {
                segT = Mathf.Clamp((queryPoint - a).Dot(ab) / abLenSq, 0f, 1f);
            }

            Vector2 proj = a + ab * segT;
            float distSq = queryPoint.DistanceSquaredTo(proj);
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                bestIdx = i;
                bestT = ((float)i + segT) / sampleCount;
            }
        }

        // Binary search refinement around bestT
        float step = 1.0f / (sampleCount * 2.0f);
        float currentT = bestT;
        for (int r = 0; r < refinementSteps; r++)
        {
            float tMinus = Mathf.Clamp(currentT - step, 0f, 1f);
            float tPlus = Mathf.Clamp(currentT + step, 0f, 1f);

            float dMinus = queryPoint.DistanceSquaredTo(Evaluate(tMinus));
            float dPlus = queryPoint.DistanceSquaredTo(Evaluate(tPlus));
            float dCurr = queryPoint.DistanceSquaredTo(Evaluate(currentT));

            if (dMinus < dCurr && dMinus < dPlus)
            {
                currentT = tMinus;
            }
            else if (dPlus < dCurr)
            {
                currentT = tPlus;
            }
            step *= 0.5f;
        }

        Vector2 closestPoint = Evaluate(currentT);
        return (currentT, closestPoint, queryPoint.DistanceTo(closestPoint));
    }

    /// <summary>
    /// Returns an array of points sampled along the curve, suitable for polyline rendering.
    /// </summary>
    public Vector2[] GetSampledPoints(int count = 16)
    {
        count = Math.Max(count, 2);
        var points = new Vector2[count + 1];
        for (int i = 0; i <= count; i++)
        {
            float t = (float)i / count;
            points[i] = Evaluate(t);
        }
        return points;
    }

    /// <summary>
    /// Computes the second derivative vector B''(t) at parameter <paramref name="t"/>.
    /// </summary>
    public Vector2 GetSecondDerivative(float t)
    {
        t = Mathf.Clamp(t, 0.0f, 1.0f);
        float u = 1.0f - t;
        // B''(t) = 6*(1-t)*(P2 - 2*P1 + P0) + 6*t*(P3 - 2*P2 + P1)
        return 6.0f * u * (P2 - 2.0f * P1 + P0) + 6.0f * t * (P3 - 2.0f * P2 + P1);
    }

    /// <summary>
    /// Computes the minimum radius of curvature R = 1 / kappa across sample points.
    /// Returns float.MaxValue for straight segments.
    /// </summary>
    public float GetMinimumRadiusOfCurvature(int samples = 16)
    {
        samples = Math.Max(samples, 4);
        float maxCurvature = 0f;

        for (int i = 0; i <= samples; i++)
        {
            float t = (float)i / samples;
            float u = 1.0f - t;

            Vector2 d1 = 3.0f * u * u * (P1 - P0) +
                         6.0f * u * t * (P2 - P1) +
                         3.0f * t * t * (P3 - P2);

            Vector2 d2 = 6.0f * u * (P2 - 2.0f * P1 + P0) +
                         6.0f * t * (P3 - 2.0f * P2 + P1);

            float speed = d1.Length();
            if (speed > 1e-4f)
            {
                // kappa = |d1.X * d2.Y - d1.Y * d2.X| / (speed^3)
                float cross = Mathf.Abs(d1.X * d2.Y - d1.Y * d2.X);
                float curvature = cross / (speed * speed * speed);
                if (curvature > maxCurvature)
                {
                    maxCurvature = curvature;
                }
            }
        }

        if (maxCurvature < 1e-5f)
        {
            return float.MaxValue;
        }

        return 1.0f / maxCurvature;
    }
}
