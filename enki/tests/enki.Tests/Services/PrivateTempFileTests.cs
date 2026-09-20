using System;
using System.Collections.Generic;
using System.IO;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Cubre <see cref="PrivateTempFile"/>, el único sitio por el que Enki escribe temporales desde
/// SEC-03/SEC-04. Los dos fallos que cierra eran de la misma familia: un nombre fijo en un directorio
/// compartido (<c>/tmp</c>, modo 1777), que otra cuenta local puede ocupar de antemano con un enlace
/// simbólico para que la app trunque un archivo ajeno; y un <c>chmod</c> aplicado <em>después</em> de
/// escribir, que deja el contenido descifrado legible por todo el mundo durante la ventana intermedia
/// (.docs/auditoria-seguridad-y-fallos-criticos.md, SEC-03 y SEC-04).
///
/// <para>Los tests corren contra el directorio privado real —es un directorio temporal, para eso está— y
/// cada uno usa un prefijo propio para no pisarse con otro que corra en paralelo.</para></summary>
public class PrivateTempFileTests : IDisposable
{
    private readonly string _prefix = $"enki-test-{Guid.NewGuid():N}-";
    private readonly List<string> _written = [];

    public void Dispose()
    {
        foreach (var path in _written)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException)
            {
                // Limpieza best-effort: un temporal que ya no está no es un fallo del test.
            }
        }
    }

    private string Track(string path)
    {
        _written.Add(path);
        return path;
    }

    [Fact]
    public void ResolveDirectory_CreatesADirectoryRestrictedToTheOwner()
    {
        var directory = PrivateTempFile.ResolveDirectory();

        Assert.True(Directory.Exists(directory));
        Assert.Null(new DirectoryInfo(directory).LinkTarget);
        Assert.StartsWith(Path.GetTempPath(), directory);

        if (!OperatingSystem.IsWindows())
        {
            // 0700: sin permiso de "ejecución" ajeno, otra cuenta no puede ni atravesar el directorio, que
            // es lo que hace seguro usar dentro nombres predecibles.
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(directory));
        }
    }

    [Fact]
    public void ResolveDirectory_IsIdempotent()
    {
        Assert.Equal(PrivateTempFile.ResolveDirectory(), PrivateTempFile.ResolveDirectory());
    }

    [Fact]
    public void Write_CreatesTheFileAlreadyRestrictedToTheOwner()
    {
        var path = Track(PrivateTempFile.Write(_prefix, ".bin", new byte[] { 1, 2, 3 }));

        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
        Assert.Equal(PrivateTempFile.ResolveDirectory(), Path.GetDirectoryName(path));
        Assert.StartsWith(_prefix, Path.GetFileName(path));
        Assert.Equal(".bin", Path.GetExtension(path));

        // Este es el assert de SEC-04: el modo es 0600 y lo es porque el archivo nació así, no porque se
        // corrigiera después. La versión anterior también acababa en 0600 — el fallo era la ventana entre
        // medias, que aquí no puede existir porque no hay dos operaciones.
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void Write_NeverReusesAName()
    {
        var first = Track(PrivateTempFile.Write(_prefix, ".bin", new byte[] { 1 }));
        var second = Track(PrivateTempFile.Write(_prefix, ".bin", new byte[] { 2 }));

        Assert.NotEqual(first, second);
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(first));
        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(second));
    }

    [Fact]
    public void Write_LeavesNothingBehindWhenTheContentFailsHalfway()
    {
        var name = _prefix + "fallido.bin";

        Assert.Throws<IOException>(() => PrivateTempFile.WriteStable(name, new FailingStream()));

        // Un temporal descifrado a medias es justo lo que no debe quedarse en disco: si la escritura no
        // termina, el archivo se borra antes de propagar el error.
        Assert.Empty(Directory.GetFiles(PrivateTempFile.ResolveDirectory(), $"{_prefix}*"));
    }

    [Fact]
    public void WriteStable_KeepsTheSameNameAndReplacesTheContent()
    {
        var name = _prefix + "stable.wav";
        var first = Track(PrivateTempFile.WriteStable(name, new MemoryStream("uno"u8.ToArray())));
        var second = PrivateTempFile.WriteStable(name, new MemoryStream("dos"u8.ToArray()));

        Assert.Equal(first, second);
        Assert.Equal("dos", File.ReadAllText(second));
        // Sin restos del archivo intermedio con el que se hizo la escritura atómica.
        Assert.Empty(Directory.GetFiles(PrivateTempFile.ResolveDirectory(), $"{name}.*.tmp"));
    }

    [Fact]
    public void CleanupStale_SweepsBothThePrivateDirectoryAndTheLegacyTempRoot()
    {
        var inPrivate = PrivateTempFile.Write(_prefix, ".bin", new byte[] { 1 });
        // Lo que dejó cualquier versión anterior a SEC-04: el temporal descifrado suelto en la raíz del
        // temporal del sistema. Si el barrido dejara de mirar ahí, esos archivos en claro se quedarían en
        // la máquina de quien actualiza para siempre.
        var legacy = Path.Combine(Path.GetTempPath(), $"{_prefix}legacy.bin");
        var unrelated = Path.Combine(Path.GetTempPath(), $"otro-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(legacy, [1]);
        File.WriteAllBytes(unrelated, [1]);

        try
        {
            PrivateTempFile.CleanupStale(_prefix, File.Delete);

            Assert.False(File.Exists(inPrivate));
            Assert.False(File.Exists(legacy));
            Assert.True(File.Exists(unrelated));
        }
        finally
        {
            if (File.Exists(unrelated)) File.Delete(unrelated);
        }
    }

    /// <summary>Un origen que revienta a mitad de la copia, para llegar al camino de error de la
    /// escritura sin depender de llenar el disco.</summary>
    private sealed class FailingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("origen ilegible");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
