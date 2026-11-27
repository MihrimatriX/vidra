namespace Vidra.Core.Playlists;

/// <summary>Süre filtresi: Kısa &lt; 4 dk, Orta 4–20 dk, Uzun &gt; 20 dk (bkz. <see cref="PlaylistFilter.BucketBounds"/>).</summary>
public enum DurationBucket { All, Short, Mid, Long }

/// <summary>Video listesinin sıralaması.</summary>
public enum VideoSort { List, Reverse, Newest, Longest, Views }

/// <summary>Liste kartlarının ("Playlistler", "Kurslar") sıralaması.</summary>
public enum ListSort { Default, Members, Missing, Count, Name }
