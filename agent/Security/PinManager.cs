using System.Security.Cryptography;
using System.Text;

namespace SoporteLan.Agent.Security;

/// <summary>
/// PIN de 6 dígitos que rota cada cierto tiempo. Es el único factor de
/// autenticación de la v1 y se muestra en la consola del equipo atendido.
/// </summary>
internal sealed class PinManager
{
    private readonly object _gate = new();
    private readonly TimeSpan _lifetime;
    private readonly string? _fixedPin;

    private string _pin = string.Empty;
    private DateTime _expiresAt;

    public PinManager(TimeSpan lifetime, string? fixedPin = null)
    {
        _lifetime = lifetime;
        _fixedPin = fixedPin;

        if (_fixedPin is not null)
        {
            _pin = _fixedPin;
            _expiresAt = DateTime.MaxValue;
        }
    }

    public string Current
    {
        get
        {
            lock (_gate)
            {
                if (_fixedPin is not null) return _fixedPin;
                if (DateTime.UtcNow >= _expiresAt) RotateCore();
                return _pin;
            }
        }
    }

    public DateTime ExpiresAt
    {
        get { lock (_gate) return _expiresAt; }
    }

    public int SecondsLeft
    {
        get
        {
            lock (_gate)
            {
                if (_fixedPin is not null) return -1;
                var left = (int)Math.Ceiling((_expiresAt - DateTime.UtcNow).TotalSeconds);
                return Math.Max(0, left);
            }
        }
    }

    public bool Verify(string candidate)
    {
        var expected = Encoding.ASCII.GetBytes(Current);
        var provided = Encoding.ASCII.GetBytes(candidate);

        // FixedTimeEquals exige el mismo largo; el resultado es constante
        // incluso cuando la longitud no coincide.
        return expected.Length == provided.Length &&
               CryptographicOperations.FixedTimeEquals(expected, provided);
    }

    private void RotateCore()
    {
        _pin = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        _expiresAt = DateTime.UtcNow.Add(_lifetime);
    }
}
