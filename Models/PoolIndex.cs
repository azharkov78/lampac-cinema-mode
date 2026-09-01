using System.Collections.Generic;

namespace CinemaMode.Models;

/// <summary>
/// On-disk index of trailers. Persisted as JSON in database/cinemamode/pool.json.
/// The list is ordered newest-first by upload_date; UI picks N random entries
/// when the operator opens Cinema Mode.
/// </summary>
public class PoolIndex
{
    public string channel { get; set; } = "";

    public string updated_at { get; set; } = "";

    public List<TrailerRecord> trailers { get; set; } = new();
}