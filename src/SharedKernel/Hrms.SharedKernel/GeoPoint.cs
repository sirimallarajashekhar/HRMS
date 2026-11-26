namespace Hrms.SharedKernel;

/// <summary>
/// A WGS84 coordinate. Stored with six decimals (about 0.1 m): finer input is rounded away from zero, which is stated here so no
/// caller is surprised by what the database would otherwise do silently.
/// </summary>
public readonly record struct GeoPoint
{
    public const int Decimals = 6;

    private GeoPoint(decimal latitude, decimal longitude)
    {
        Latitude = latitude;
        Longitude = longitude;
    }

    public decimal Latitude { get; }

    public decimal Longitude { get; }

    public static Result<GeoPoint> Create(decimal latitude, decimal longitude)
    {
        if (latitude is < -90m or > 90m)
        {
            return Result.Failure<GeoPoint>(Error.Validation("geo.latitude_invalid", "Latitude is between -90 and 90 degrees."));
        }

        if (longitude is < -180m or > 180m)
        {
            return Result.Failure<GeoPoint>(Error.Validation("geo.longitude_invalid", "Longitude is between -180 and 180 degrees."));
        }

        return Result.Success(new GeoPoint(
            decimal.Round(latitude, Decimals, MidpointRounding.AwayFromZero),
            decimal.Round(longitude, Decimals, MidpointRounding.AwayFromZero)));
    }
}
