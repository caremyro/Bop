using System;

namespace Bop;

public class QueueItem
{
    // Identifiant stable : permet de retrouver/supprimer un élément même si
    // la liste a bougé entre le survol et le clic (plus fiable qu'un index brut).
    public Guid Id { get; } = Guid.NewGuid();
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = "Loading...";
    public string Channel { get; set; } = "";
    public TimeSpan Duration { get; set; } = TimeSpan.Zero;
}