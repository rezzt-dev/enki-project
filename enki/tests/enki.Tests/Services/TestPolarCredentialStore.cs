using System;
using System.IO;
using enki.Services;

namespace enki.Tests.Services;

internal sealed class TestPolarCredentialStore : IPolarCredentialStore
{
    public PolarCredentials? Credentials { get; private set; }
    public bool FailReads { get; set; }
    public bool FailWrites { get; set; }

    public PolarCredentials? Read() => FailReads
        ? throw new IOException("Almacén de prueba no disponible.") : Credentials;

    public void Write(PolarCredentials credentials)
    {
        if (FailWrites) throw new IOException("Almacén de prueba no disponible.");
        Credentials = credentials;
    }

    public void Delete()
    {
        if (FailWrites) throw new IOException("Almacén de prueba no disponible.");
        Credentials = null;
    }
}
