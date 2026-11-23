using System.Globalization;
using System.Numerics;
using System.Text;

namespace Cerebral.Tools;

internal static class MathCalculator
{
    public static object Calculate(string expression, int decimalPlaces, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(expression) || expression.Length > 4096) throw new ArgumentException("Expression must contain 1..4096 characters.");
        if (decimalPlaces is < 0 or > 50) throw new ArgumentException("decimalPlaces must be 0..50.");
        var value = new Parser(expression, token).Parse();
        var whole = BigInteger.DivRem(BigInteger.Abs(value.Numerator), value.Denominator, out var remainder);
        var decimalText = new StringBuilder(value.Numerator.Sign < 0 ? "-" : "").Append(whole.ToString(CultureInfo.InvariantCulture));
        if (decimalPlaces > 0 && !remainder.IsZero)
        {
            decimalText.Append('.');
            for (var i = 0; i < decimalPlaces && !remainder.IsZero; i++)
            {
                var digit = BigInteger.DivRem(remainder * 10, value.Denominator, out remainder);
                decimalText.Append(digit.ToString(CultureInfo.InvariantCulture));
            }
        }
        var numerator = value.Numerator.ToString(CultureInfo.InvariantCulture);
        var denominator = value.Denominator.ToString(CultureInfo.InvariantCulture);
        return new { expression, numerator, denominator, exact = value.Denominator.IsOne ? numerator : numerator + "/" + denominator,
            @decimal = decimalText.ToString(), decimalPlaces, decimalTruncated = !remainder.IsZero };
    }

    private readonly record struct Rational
    {
        public BigInteger Numerator { get; }
        public BigInteger Denominator { get; }
        public Rational(BigInteger numerator, BigInteger denominator)
        {
            if (denominator.IsZero) throw new DivideByZeroException("Division by zero.");
            if (denominator.Sign < 0) { numerator = -numerator; denominator = -denominator; }
            var gcd = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);
            numerator /= gcd; denominator /= gcd;
            if (BigInteger.Abs(numerator).GetBitLength() > 16384 || denominator.GetBitLength() > 16384)
                throw new ArgumentException("Calculation exceeds the numeric size limit.");
            Numerator = numerator; Denominator = denominator;
        }
        public static Rational operator +(Rational a, Rational b) => new(a.Numerator * b.Denominator + b.Numerator * a.Denominator, a.Denominator * b.Denominator);
        public static Rational operator -(Rational a, Rational b) => new(a.Numerator * b.Denominator - b.Numerator * a.Denominator, a.Denominator * b.Denominator);
        public static Rational operator *(Rational a, Rational b) => new(a.Numerator * b.Numerator, a.Denominator * b.Denominator);
        public static Rational operator /(Rational a, Rational b) => new(a.Numerator * b.Denominator, a.Denominator * b.Numerator);
    }

    private sealed class Parser(string source, CancellationToken token)
    {
        private int position, depth, operations;
        public Rational Parse()
        {
            var result = Expression(); Space();
            if (position != source.Length) throw Error("Unexpected character");
            return result;
        }
        private Rational Expression()
        {
            var value = Term();
            while (true)
            {
                if (Take('+')) { Step(); value += Term(); }
                else if (Take('-')) { Step(); value -= Term(); }
                else return value;
            }
        }
        private Rational Term()
        {
            var value = Unary();
            while (true)
            {
                if (Take('*')) { Step(); value *= Unary(); }
                else if (Take('/')) { Step(); value /= Unary(); }
                else return value;
            }
        }
        private Rational Unary()
        {
            if (++depth > 64) throw Error("Nesting exceeds 64 levels");
            try
            {
                if (Take('+')) { Step(); return Unary(); }
                if (Take('-')) { Step(); var value = Unary(); return new(-value.Numerator, value.Denominator); }
                return Primary();
            }
            finally { depth--; }
        }
        private Rational Primary()
        {
            if (Take('(')) { var value = Expression(); Expect(')'); return value; }
            Space();
            if (position < source.Length && char.IsAsciiLetter(source[position]))
            {
                var start = position;
                while (position < source.Length && char.IsAsciiLetter(source[position])) position++;
                var name = source[start..position]; Step(); Expect('(');
                var n = Integral(Expression());
                int k = 0;
                if (name == "choose") { Expect(','); k = Integral(Expression()); }
                else if (name != "factorial") throw Error("Only choose(n,k) and factorial(n) are supported");
                Expect(')');
                BigInteger result = 1;
                if (name == "choose")
                {
                    if (k > n) throw Error("choose requires 0 <= k <= n");
                    k = Math.Min(k, n - k);
                    for (var i = 1; i <= k; i++) { token.ThrowIfCancellationRequested(); result = result * (n - i + 1) / i; }
                }
                else for (var i = 2; i <= n; i++) { token.ThrowIfCancellationRequested(); result *= i; }
                return new(result, BigInteger.One);
            }
            var numberStart = position; var digits = 0; var fractionalDigits = 0;
            while (position < source.Length && char.IsAsciiDigit(source[position])) { position++; digits++; }
            if (position < source.Length && source[position] == '.')
            {
                position++;
                while (position < source.Length && char.IsAsciiDigit(source[position])) { position++; digits++; fractionalDigits++; }
            }
            if (digits is < 1 or > 256) throw Error("Numeric literals require 1..256 digits");
            var literal = source[numberStart..position].Replace(".", "", StringComparison.Ordinal);
            return new(BigInteger.Parse(literal, CultureInfo.InvariantCulture), BigInteger.Pow(10, fractionalDigits));
        }
        private static int Integral(Rational value)
        {
            if (!value.Denominator.IsOne || value.Numerator < 0 || value.Numerator > 1000)
                throw new ArgumentException("Function arguments must be integers in 0..1000.");
            return (int)value.Numerator;
        }
        private void Step()
        {
            token.ThrowIfCancellationRequested();
            if (++operations > 256) throw Error("Calculation exceeds 256 operations");
        }
        private void Space() { while (position < source.Length && char.IsWhiteSpace(source[position])) position++; }
        private bool Take(char value)
        {
            Space();
            if (position >= source.Length || source[position] != value) return false;
            position++; return true;
        }
        private void Expect(char value) { if (!Take(value)) throw Error("Expected '" + value + "'"); }
        private ArgumentException Error(string message) => new(message + " at position " + position.ToString(CultureInfo.InvariantCulture) + ".");
    }
}
