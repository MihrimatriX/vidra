using System.Text.Json.Serialization;

namespace Vidra.Core.Models;

/// <summary>yt-dlp'nin YouTube oturumunu nereden alacağı.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(NoCookies), "none")]
[JsonDerivedType(typeof(BrowserCookies), "browser")]
[JsonDerivedType(typeof(FileCookies), "file")]
[JsonDerivedType(typeof(AppLoginCookies), "app")]
public abstract record CookieSource;

public sealed record NoCookies : CookieSource;

public sealed record BrowserCookies(BrowserKind Browser, string? Profile = null) : CookieSource;

/// <summary>Kullanıcının dışarıdan verdiği Netscape biçimli cookies.txt.</summary>
public sealed record FileCookies(string Path) : CookieSource;

/// <summary>
/// Uygulama içi giriş penceresinden (WebView2) dışa aktarılan oturum.
/// Tarayıcıdan ayrı bir oturum olduğu için YouTube'un çerez yenilemesinden etkilenmez.
/// </summary>
public sealed record AppLoginCookies(string Path, DateTimeOffset ExportedAt, DateTimeOffset? ExpiresAt) : CookieSource;
