using System;
using System.IO;
using System.Reflection;
using System.Threading;
using MediaBrowser.Model.Logging;

namespace EmbyProxyRouter.Patch
{
    /// <summary>
    /// Makes the embedded Harmony assembly loadable at runtime.
    /// </summary>
    /// <remarks>
    /// Harmony ships inside this DLL as an embedded resource rather than as a second file, so that
    /// deployment stays "drop one DLL into /config/plugins" — which is how the Emby plugin folder is
    /// normally managed, and one less file to get wrong on a permissions-sensitive Unraid share.
    ///
    /// The resolver must be installed before any method that mentions a Harmony type gets JIT-compiled,
    /// which is why callers must keep Harmony usage behind a NoInlining boundary.
    /// </remarks>
    internal static class HarmonyLoader
    {
        private const string ResourceName = "EmbyProxyRouter.Embedded.0Harmony.dll";
        private const string AssemblyName = "0Harmony";

        private static int _installed;
        private static Assembly _harmony;

        public static void EnsureResolverInstalled(ILogger logger)
        {
            if (Interlocked.Exchange(ref _installed, 1) == 1)
            {
                return;
            }

            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                string requested;
                try
                {
                    requested = new AssemblyName(args.Name).Name;
                }
                catch (Exception)
                {
                    return null;
                }

                if (!string.Equals(requested, AssemblyName, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                var existing = Volatile.Read(ref _harmony);
                if (existing != null)
                {
                    return existing;
                }

                try
                {
                    var self = typeof(HarmonyLoader).Assembly;
                    using (var stream = self.GetManifestResourceStream(ResourceName))
                    {
                        if (stream == null)
                        {
                            logger.Error("Embedded Harmony resource '" + ResourceName +
                                         "' not found - the patch cannot be applied.");
                            return null;
                        }

                        using (var buffer = new MemoryStream())
                        {
                            stream.CopyTo(buffer);
                            var loaded = Assembly.Load(buffer.ToArray());
                            Volatile.Write(ref _harmony, loaded);
                            logger.Debug("Harmony loaded from embedded resource: " +
                                         loaded.FullName);
                            return loaded;
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.ErrorException("Harmony could not be loaded.", ex);
                    return null;
                }
            };
        }
    }
}
