namespace PipelineProcessor.Services;

// ============================================================================
// IntegrityCalculator
// ----------------------------------------------------------------------------
// This is the engineering core of the project: a pure implementation of the
// Modified ASME B31G formula.
//
// "Pure" means:
//   - numbers go in, numbers come out;
//   - no database, HTTP, file system, clock or logger;
//   - the same inputs always produce the same outputs.
//
// That makes the maths easy to unit test and keeps it independent of how the
// data was stored or where the request came from. AssessmentJob loads the
// pipeline and anomalies, calls this class, and saves the results.
//
// Formula (from ROADMAP.md):
//
//   flowStress    = SMYS + 69
//   z             = L² / (D · t)
//   M             = z <= 50 ? sqrt(1 + 0.6275·z − 0.003375·z²)
//                            : 0.032·z + 3.3
//   failStress    = flowStress · (1 − 0.85·d/t) / (1 − 0.85·(d/t)/M)
//   failPressure  = 2 · failStress · t / D
//   safePressure  = failPressure · 0.72
//   ERF           = MAOP / safePressure
//
// ERF stands for Estimated Repair Factor. An ERF of 1 or more means the defect
// must be repaired; a value below 1 means it is currently acceptable.
// ============================================================================

/// <summary>
/// The physical properties of a pipeline that the calculation needs.
/// All values must be positive. Units are part of the name on purpose.
/// </summary>
public readonly record struct PipeProperties(
    double OuterDiameterMm,
    double WallThicknessMm,
    double SmysMpa,
    double MaopMpa);

/// <summary>
/// The result of one B31G calculation.
/// </summary>
public readonly record struct IntegrityResult(
    double FailurePressureMpa,
    double SafePressureMpa,
    double Erf);

/// <summary>
/// Pure Modified ASME B31G calculations. No dependencies, so it is safe to call
/// from a background job and simple to test in isolation.
/// </summary>
public static class IntegrityCalculator
{
    /// <summary>The name stored in the Assessments table.</summary>
    public const string MethodName = "ModifiedB31G";

    /// <summary>
    /// Design factor for a Class 1 location, as specified by the project.
    /// A smaller factor makes the safe pressure more conservative.
    /// </summary>
    public const double DesignFactor = 0.72;

    // The formula switches from the parabolic bulging factor to the linear
    // approximation at z = 50.
    private const double BulgingFactorSwitch = 50.0;

    /// <summary>
    /// Calculates the failure pressure and ERF for one defect.
    /// </summary>
    /// <param name="pipe">Diameter, wall thickness, steel strength and MAOP.</param>
    /// <param name="depthMm">
    /// Defect depth in millimetres. The spreadsheet stores depth as a percentage
    /// of wall thickness, so the caller converts it first:
    /// <c>depthMm = depthPct / 100 · WallThicknessMm</c>.
    /// </param>
    /// <param name="lengthMm">Defect length along the pipe, in millimetres.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the pipe or defect values are physically invalid. Failing
    /// loudly is better than saving a meaningless assessment.
    /// </exception>
    public static IntegrityResult Calculate(
        PipeProperties pipe,
        double depthMm,
        double lengthMm)
    {
        Validate(pipe, depthMm, lengthMm);

        // Flow stress is the effective strength of the steel used by B31G.
        var flowStressMpa = pipe.SmysMpa + 69.0;

        // z describes how "long and shallow" the defect is relative to the pipe.
        // It is a dimensionless number.
        var z = lengthMm * lengthMm / (pipe.OuterDiameterMm * pipe.WallThicknessMm);

        // M is the bulging factor. A longer defect allows the pipe wall to bulge
        // out more, which lowers the failure pressure.
        var bulgingFactor = z <= BulgingFactorSwitch
            ? Math.Sqrt(1.0 + 0.6275 * z - 0.003375 * z * z)
            : 0.032 * z + 3.3;

        // d/t is the fraction of the wall that has been lost.
        var depthRatio = depthMm / pipe.WallThicknessMm;

        // Remaining strength of the defect cross-section.
        var failureStressMpa =
            flowStressMpa * (1.0 - 0.85 * depthRatio) /
            (1.0 - 0.85 * depthRatio / bulgingFactor);

        // Convert stress (force per area) into the internal pressure that would
        // burst the pipe. This is the pressure the pipe can still withstand.
        var failurePressureMpa =
            2.0 * failureStressMpa * pipe.WallThicknessMm / pipe.OuterDiameterMm;

        // Apply the design factor. The pipeline is only allowed to operate at
        // this reduced "safe" pressure according to the chosen class.
        var safePressureMpa = failurePressureMpa * DesignFactor;

        // If the actual operating pressure (MAOP) is at or above the safe
        // pressure, ERF is at least 1 and the defect needs repair.
        var erf = pipe.MaopMpa / safePressureMpa;

        return new IntegrityResult(failurePressureMpa, safePressureMpa, erf);
    }

    /// <summary>
    /// Convenience method for callers that only need the ERF value.
    /// </summary>
    public static double CalculateErf(
        PipeProperties pipe,
        double depthMm,
        double lengthMm)
    {
        return Calculate(pipe, depthMm, lengthMm).Erf;
    }

    private static void Validate(
        PipeProperties pipe,
        double depthMm,
        double lengthMm)
    {
        if (!double.IsFinite(pipe.OuterDiameterMm) || pipe.OuterDiameterMm <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pipe),
                "Outer diameter must be a positive, finite number.");
        }

        if (!double.IsFinite(pipe.WallThicknessMm) || pipe.WallThicknessMm <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pipe),
                "Wall thickness must be a positive, finite number.");
        }

        if (!double.IsFinite(pipe.SmysMpa) || pipe.SmysMpa <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pipe),
                "SMYS must be a positive, finite number.");
        }

        if (!double.IsFinite(pipe.MaopMpa) || pipe.MaopMpa <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pipe),
                "MAOP must be a positive, finite number.");
        }

        if (!double.IsFinite(depthMm) || depthMm < 0 || depthMm > pipe.WallThicknessMm)
        {
            throw new ArgumentOutOfRangeException(
                nameof(depthMm),
                "Defect depth must be between zero and the wall thickness.");
        }

        if (!double.IsFinite(lengthMm) || lengthMm <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lengthMm),
                "Defect length must be a positive, finite number.");
        }
    }
}
