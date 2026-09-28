using System;

namespace InFalsusChartLoader
{
    /// <summary>
    /// Turning an exception the runtime handed back into something readable.
    ///
    /// <para>
    /// A method reached through `il2cpp_runtime_invoke` does not throw on this side. It returns the
    /// exception as a value in its last parameter, and code that ignores it turns every failure into
    /// a silent wrong answer — which is the failure mode this repository keeps running into.
    /// </para>
    /// <para>
    /// This mod calls three of the game's own methods that way: the song list refresh, the pack
    /// lookup rebuild, and the handful of rendering calls a jacket needs. What each of them did wrong
    /// is the entire diagnostic value of a failed run, so it is read out rather than summarised.
    /// </para>
    /// </summary>
    internal static unsafe class Raised
    {
        /// <summary>
        /// The exception's type and message, or null when there was none.
        ///
        /// `ToString` is called on the exception itself rather than reading its fields: an IL2CPP
        /// exception is the game's own `System.Exception`, and the message can come from a derived
        /// type's override — the same reason a C# log would print `e` and not `e.Message`.
        ///
        /// The name is what a failure has to say when `ToString` cannot be reached, so it is read
        /// through the interop assembly's UTF-8 helper rather than by treating a raw name pointer as
        /// an IL2CPP string. This is the path every jacket failure takes, and it used to answer with
        /// nothing at all — an exception whose name could not be read reads exactly like no
        /// exception, which is what kept a rendering failure looking like a mystery.
        /// </summary>
        internal static string Text(IntPtr exception)
        {
            if (exception == IntPtr.Zero) return null;

            try
            {
                IntPtr klass = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(exception);
                if (klass == IntPtr.Zero) return "<exception with no class>";

                IntPtr toString = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_method_from_name(klass, "ToString", 0);
                if (toString != IntPtr.Zero)
                {
                    IntPtr again = IntPtr.Zero;
                    IntPtr text = Il2CppInterop.Runtime.IL2CPP.il2cpp_runtime_invoke(toString, exception, null, ref again);

                    if (again == IntPtr.Zero && text != IntPtr.Zero)
                    {
                        string rendered = Il2CppInterop.Runtime.IL2CPP.Il2CppStringToManaged(text);
                        if (!string.IsNullOrEmpty(rendered)) return rendered;
                    }
                }

                return ClassName(klass);
            }
            catch (Exception e)
            {
                return $"<exception could not be read: {Diagnostics.Describe(e)}>";
            }
        }

        /// <summary>
        /// The exception's class name. Any answer beats none here: this is the last thing a failed
        /// run has to report, so every fallback names what happened rather than returning null.
        /// </summary>
        private static string ClassName(IntPtr klass)
        {
            try
            {
                string name = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_name_(klass);
                if (!string.IsNullOrEmpty(name)) return name;
            }
            catch (Exception)
            {
                // Falls through to the literal below.
            }
            return "<exception whose class could not be named>";
        }
    }
}
