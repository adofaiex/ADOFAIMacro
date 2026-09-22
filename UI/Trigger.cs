#nullable enable
using System;

namespace ADOFAIMacro.UI
{
    /// <summary>键值变化时惰性重建的缓存触发器（移植自 Iridium.Utilities.Trigger）。</summary>
    internal sealed class Trigger<TK, TV>
    {
        private bool _initialized;
        private TK? _key;
        private TV? _value;
        private readonly Func<TK, TV>? _initializer;

        public Trigger(Func<TK, TV>? initializer = null) => _initializer = initializer;

        public TV ResetWithOld()
        {
            var old = _value;
            _initialized = false;
            _key = default;
            _value = default;
            return old!;
        }

        public TV Get(TK key, Func<TK, TV>? initializer = null)
        {
            if (!_initialized || !Equals(key, _key))
            {
                initializer ??= _initializer;
                _value = initializer!(key);
                _key = key;
                _initialized = true;
            }
            return _value!;
        }
    }
}
