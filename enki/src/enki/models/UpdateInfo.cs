using System;

namespace enki.Models;

/// <summary>Una release más reciente que la instalada (T9 de
/// .docs/funcionalidades-pendientes-priorizadas.md §4.5). Solo información para enseñar y un enlace que
/// abrir en el navegador: Enki no descarga ni ejecuta nada, y el propio documento de la funcionalidad lo
/// fija como requisito ("solo aviso, no auto-actualización").</summary>
/// <param name="Version">Versión de la release, normalizada sin la <c>v</c> de la etiqueta.</param>
/// <param name="Name">Título de la release, o la versión si GitHub no trae uno.</param>
/// <param name="Url">Página de la release en el repositorio oficial — la comprueba
/// <c>Services.UpdateCheckService</c> antes de devolverla, y <c>UrlOpener</c> la vuelve a filtrar.</param>
/// <param name="Notes">Notas de la versión tal cual las publica la release, recortadas.</param>
/// <param name="PublishedAtUtc">Fecha de publicación, o null si la respuesta no la traía.</param>
public sealed record UpdateInfo(string Version, string Name, string Url, string Notes, DateTime? PublishedAtUtc);
