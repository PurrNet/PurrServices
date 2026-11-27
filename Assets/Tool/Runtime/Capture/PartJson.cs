using System.Globalization;
using System.Text;

namespace PurrNet.Services.Capture
{
    /// <summary>Builds the small JSON texts a part file carries (its header, notes and footer).</summary>
    public sealed class PartJson
    {
        readonly StringBuilder _text = new StringBuilder(256);
        bool _needsComma;

        public PartJson BeginObject() => Open('{');

        public PartJson EndObject() => Close('}');

        public PartJson BeginArray() => Open('[');

        public PartJson EndArray() => Close(']');

        public PartJson Key(string name)
        {
            Separate();
            Quote(name);
            _text.Append(':');
            _needsComma = false;
            return this;
        }

        public PartJson Prop(string name, string value)
        {
            Key(name);
            if (value == null)
                _text.Append("null");
            else
                Quote(value);
            _needsComma = true;
            return this;
        }

        public PartJson Prop(string name, long value)
        {
            Key(name);
            _text.Append(value.ToString(CultureInfo.InvariantCulture));
            _needsComma = true;
            return this;
        }

        public PartJson Prop(string name, double value)
        {
            Key(name);
            _text.Append(double.IsNaN(value) || double.IsInfinity(value)
                ? "0"
                : value.ToString("R", CultureInfo.InvariantCulture));
            _needsComma = true;
            return this;
        }

        public PartJson Prop(string name, bool value)
        {
            Key(name);
            _text.Append(value ? "true" : "false");
            _needsComma = true;
            return this;
        }

        /// <summary>A property whose value is JSON already.</summary>
        public PartJson Raw(string name, string json)
        {
            Key(name);
            _text.Append(string.IsNullOrEmpty(json) ? "null" : json);
            _needsComma = true;
            return this;
        }

        /// <summary>A number in an array.</summary>
        public PartJson Value(double value)
        {
            Separate();
            _text.Append(double.IsNaN(value) || double.IsInfinity(value)
                ? "0"
                : value.ToString("R", CultureInfo.InvariantCulture));
            _needsComma = true;
            return this;
        }

        /// <summary>A text in an array.</summary>
        public PartJson Value(string value)
        {
            Separate();
            if (value == null)
                _text.Append("null");
            else
                Quote(value);
            _needsComma = true;
            return this;
        }

        public int length => _text.Length;

        public override string ToString() => _text.ToString();

        PartJson Open(char bracket)
        {
            Separate();
            _text.Append(bracket);
            _needsComma = false;
            return this;
        }

        PartJson Close(char bracket)
        {
            _text.Append(bracket);
            _needsComma = true;
            return this;
        }

        void Separate()
        {
            if (_needsComma)
                _text.Append(',');
        }

        void Quote(string value)
        {
            _text.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"': _text.Append("\\\""); break;
                    case '\\': _text.Append("\\\\"); break;
                    case '\n': _text.Append("\\n"); break;
                    case '\r': _text.Append("\\r"); break;
                    case '\t': _text.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            _text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            _text.Append(c);
                        break;
                }
            }
            _text.Append('"');
        }
    }
}
