using System;
using System.Runtime.InteropServices;
using MelonLoader;

namespace NoImNotAHumanAccess.Interop
{
    /// <summary>
    /// Works around a Unity ENGINE crash (Unity issue UUM-126552, fixed in 6000.3.14f1 — this game ships
    /// 6000.3.10f1) by neutering UnityPlayer's single call to UIAutomationCore!UiaRaiseAutomationEvent.
    ///
    /// The crash, from the reporter's minidump (symbolized against Unity's symbol server):
    ///     ucrtbase!abort
    ///     ucrtbase!purecall                                  <-- virtual call on a DESTROYED object
    ///     UIAutomationCore!GetProviderEventInfo
    ///     UIAutomationCore!UiaRaiseAutomationEvent
    ///     UnityPlayer!AnalyticsSessionService::OnPlayerSessionStateChanged
    ///     UnityPlayer!AnalyticsSessionService::OnPlayerStateChanged
    ///     UnityPlayer!CallbackArray1<bool>::Invoke
    ///     UnityPlayer!PlayerWindowActivated                  <-- alt-tab
    ///     UnityPlayer!PlayerMainWndProc
    ///     user32!... -> GetMessageW
    /// Alt-tab raises WM_ACTIVATE; Unity's analytics session callback calls UiaRaiseAutomationEvent on a UIA
    /// provider whose vtable is already torn down; the CRT's purecall handler aborts the process
    /// (0xC0000409 subcode 7 = FAST_FAIL_FATAL_APP_EXIT — NOT a stack buffer overrun despite Windows' label).
    /// Windows 10 only; not reproducible on Windows 11.
    ///
    /// Fix approach: patch UnityPlayer.dll's IMPORT ADDRESS TABLE entries for the UIA event-RAISING functions to
    /// point at our own stubs, which return UIA_E_ELEMENTNOTAVAILABLE without touching the provider pointer. The
    /// dead object is never dereferenced, so purecall can't fire. We do NOT detour inside UIAutomationCore itself —
    /// patching one importer's IAT is narrower (only UnityPlayer is affected; NVDA/JAWS/other UIA clients in this
    /// process keep the real function) and needs no detour library.
    ///
    /// UnityPlayer imports SEVEN UIA functions. We suppress the four that RAISE events — each one hands a provider
    /// pointer to UIAutomationCore, so each is a candidate for the same use-after-free. Only UiaRaiseAutomationEvent
    /// is named in the one dump we have; the other three are covered because the same destroyed-provider bug could
    /// reach them and we cannot reproduce the crash locally to find out (Win10-only). The remaining three imports are
    /// deliberately LEFT ALONE:
    ///   • UiaHostProviderFromHwnd / UiaReturnRawElementProvider — these SERVE queries from real UIA clients
    ///     (Narrator/JAWS reading the window). Suppressing them would break other tools' access for no benefit;
    ///     they don't raise events and aren't on the crash path.
    ///   • UiaClientsAreListening — a pure predicate, harmless, and lying about it could change engine behaviour
    ///     in ways the dump gives us no reason to want.
    ///
    /// Why suppressing is safe HERE: this mod deliberately does not use Unity's UIA/AssistiveSupport path at all
    /// (it speaks through UniversalSpeech directly — see UalAnnouncer) because the IL2CPP build stripped
    /// AccessibilityNode/Hierarchy out of GameAssembly.dll, so the engine exposes no useful UIA tree to suppress.
    /// The only caller is Unity's analytics session tracking, which discards the result.
    ///
    /// Returns UIA_E_ELEMENTNOTAVAILABLE rather than S_OK: it is the truthful answer (the element genuinely is
    /// not available) and is a documented, expected return that callers must already handle.
    ///
    /// NOTE: this masks an engine bug for OUR users only — players without the mod still crash. The real fix is
    /// Trioskaz shipping Unity 6000.3.14f1+. Disable via MelonPreferences once that lands.
    /// </summary>
    internal static class UiaEventSuppressor
    {
        // The four event-raising imports, each with its own signature (all __stdcall, all returning HRESULT).
        // Parameters are never dereferenced by our stubs — that is the entire point.
        //   HRESULT UiaRaiseAutomationEvent(IRawElementProviderSimple *pProvider, EVENTID id)
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int UiaRaiseAutomationEventFn(IntPtr provider, int eventId);

        //   HRESULT UiaRaiseStructureChangedEvent(IRawElementProviderSimple *pProvider, StructureChangeType structureChangeType,
        //                                         int *pRuntimeId, int cRuntimeIdLen)
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int UiaRaiseStructureChangedEventFn(IntPtr provider, int changeType, IntPtr runtimeId, int runtimeIdLen);

        //   HRESULT UiaRaiseNotificationEvent(IRawElementProviderSimple *provider, NotificationKind notificationKind,
        //                                     NotificationProcessing notificationProcessing, BSTR displayString, BSTR activityId)
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int UiaRaiseNotificationEventFn(IntPtr provider, int kind, int processing, IntPtr displayString, IntPtr activityId);

        //   HRESULT UiaRaiseAutomationPropertyChangedEvent(IRawElementProviderSimple *pProvider, PROPERTYID id,
        //                                                  VARIANT oldValue, VARIANT newValue)
        //   VARIANT is 16 bytes and passed BY VALUE. On x64 __stdcall a 16-byte struct is passed indirectly (by
        //   pointer), so IntPtr matches the ABI for the caller's perspective. We never read them.
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int UiaRaiseAutomationPropertyChangedEventFn(IntPtr provider, int propertyId, IntPtr oldValue, IntPtr newValue);

        private const int UIA_E_ELEMENTNOTAVAILABLE = unchecked((int)0x80040201);

        // Keep the delegates alive for the process lifetime: the IAT holds raw pointers to their thunks, and if a
        // delegate is collected its thunk is freed and UnityPlayer calls into freed memory — a worse crash than
        // the one we're fixing.
        private static readonly System.Collections.Generic.List<Delegate> _keepAlive = new System.Collections.Generic.List<Delegate>();
        private static bool _applied;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string moduleName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint newProtect, out uint oldProtect);

        private const uint PAGE_READWRITE = 0x04;

        // The stubs. Every parameter is ignored — never dereference a provider pointer that may already be dead.
        private static int RaiseAutomationEventStub(IntPtr provider, int eventId) => UIA_E_ELEMENTNOTAVAILABLE;
        private static int RaiseStructureChangedEventStub(IntPtr p, int t, IntPtr rid, int len) => UIA_E_ELEMENTNOTAVAILABLE;
        private static int RaiseNotificationEventStub(IntPtr p, int k, int pr, IntPtr d, IntPtr a) => UIA_E_ELEMENTNOTAVAILABLE;
        private static int RaisePropertyChangedEventStub(IntPtr p, int id, IntPtr o, IntPtr n) => UIA_E_ELEMENTNOTAVAILABLE;

        /// <summary>
        /// Point UnityPlayer.dll's UIA event-raising imports at inert stubs.
        /// Fails open (logs and returns false) — never throws into MelonLoader's init.
        /// Returns true if at least one import was redirected.
        /// </summary>
        public static bool Apply(MelonLogger.Instance log)
        {
            if (_applied) return true;
            try
            {
                IntPtr unityPlayer = GetModuleHandleW("UnityPlayer.dll");
                if (unityPlayer == IntPtr.Zero)
                {
                    log.Warning("[UiaSuppress] UnityPlayer.dll not loaded; alt-tab crash workaround NOT applied.");
                    return false;
                }

                int patched = 0;
                // UiaRaiseAutomationEvent is the one the crash dump actually names; the rest are defence in depth.
                patched += Redirect(log, unityPlayer, "UiaRaiseAutomationEvent",
                    (UiaRaiseAutomationEventFn)RaiseAutomationEventStub) ? 1 : 0;
                patched += Redirect(log, unityPlayer, "UiaRaiseStructureChangedEvent",
                    (UiaRaiseStructureChangedEventFn)RaiseStructureChangedEventStub) ? 1 : 0;
                patched += Redirect(log, unityPlayer, "UiaRaiseNotificationEvent",
                    (UiaRaiseNotificationEventFn)RaiseNotificationEventStub) ? 1 : 0;
                patched += Redirect(log, unityPlayer, "UiaRaiseAutomationPropertyChangedEvent",
                    (UiaRaiseAutomationPropertyChangedEventFn)RaisePropertyChangedEventStub) ? 1 : 0;

                if (patched == 0)
                {
                    // Expected on a build that doesn't import them — e.g. 6000.3.14f1+ where the bug is fixed.
                    log.Msg("[UiaSuppress] No UIA event-raising imports found in UnityPlayer.dll; nothing to do.");
                    return false;
                }

                _applied = true;
                log.Msg($"[UiaSuppress] Suppressed {patched} UnityPlayer UIA event-raising import(s); "
                      + "alt-tab crash (Unity UUM-126552) worked around.");
                return true;
            }
            catch (Exception e)
            {
                log.Warning($"[UiaSuppress] Workaround not applied: {e.Message}");
                return false;
            }
        }

        /// <summary>Repoint one IAT slot at <paramref name="stub"/>. Returns false (logged) if not found/not writable.</summary>
        private static bool Redirect(MelonLogger.Instance log, IntPtr module, string function, Delegate stub)
        {
            IntPtr slot = FindImportSlot(module, "UIAutomationCore.dll", function);
            if (slot == IntPtr.Zero)
            {
                log.Msg($"[UiaSuppress] {function}: not imported; skipped.");
                return false;
            }

            // Root the delegate BEFORE its pointer goes into the IAT — once written, native code may call it at any time.
            _keepAlive.Add(stub);
            IntPtr stubPtr = Marshal.GetFunctionPointerForDelegate(stub);

            if (!VirtualProtect(slot, (UIntPtr)IntPtr.Size, PAGE_READWRITE, out uint old))
            {
                log.Warning($"[UiaSuppress] {function}: VirtualProtect failed (err {Marshal.GetLastWin32Error()}); skipped.");
                _keepAlive.Remove(stub);
                return false;
            }

            IntPtr original = Marshal.ReadIntPtr(slot);
            Marshal.WriteIntPtr(slot, stubPtr);
            VirtualProtect(slot, (UIntPtr)IntPtr.Size, old, out _);

            log.Msg($"[UiaSuppress] {function}: 0x{original.ToInt64():X} -> 0x{stubPtr.ToInt64():X}");
            return true;
        }

        /// <summary>
        /// Walk <paramref name="module"/>'s PE import directory and return the address of the IAT slot holding
        /// <paramref name="function"/> imported from <paramref name="fromDll"/>, or IntPtr.Zero.
        /// </summary>
        private static IntPtr FindImportSlot(IntPtr module, string fromDll, string function)
        {
            long baseAddr = module.ToInt64();

            // IMAGE_DOS_HEADER.e_lfanew @ 0x3C -> IMAGE_NT_HEADERS
            int ntOffset = Marshal.ReadInt32(module, 0x3C);
            IntPtr nt = new IntPtr(baseAddr + ntOffset);
            if (Marshal.ReadInt32(nt) != 0x00004550) return IntPtr.Zero; // "PE\0\0"

            // IMAGE_NT_HEADERS: Signature(4) + IMAGE_FILE_HEADER(20) -> OptionalHeader.
            // Magic 0x20B = PE32+; DataDirectory sits at OptionalHeader+0x70 there. We only support x64.
            IntPtr optional = new IntPtr(nt.ToInt64() + 4 + 20);
            if (Marshal.ReadInt16(optional) != 0x20B) return IntPtr.Zero;

            // DataDirectory[1] = Import Directory (each entry: RVA(4) + Size(4)).
            int importRva = Marshal.ReadInt32(new IntPtr(optional.ToInt64() + 0x70 + (1 * 8)));
            if (importRva == 0) return IntPtr.Zero;

            // IMAGE_IMPORT_DESCRIPTOR: OriginalFirstThunk(0) TimeDateStamp(4) ForwarderChain(8) Name(12) FirstThunk(16)
            for (long desc = baseAddr + importRva; ; desc += 20)
            {
                int originalFirstThunk = Marshal.ReadInt32(new IntPtr(desc + 0));
                int nameRva = Marshal.ReadInt32(new IntPtr(desc + 12));
                int firstThunk = Marshal.ReadInt32(new IntPtr(desc + 16));
                if (originalFirstThunk == 0 && firstThunk == 0 && nameRva == 0) break; // null terminator

                string? dllName = Marshal.PtrToStringAnsi(new IntPtr(baseAddr + nameRva));
                if (dllName == null || !dllName.Equals(fromDll, StringComparison.OrdinalIgnoreCase)) continue;

                // Names live in OriginalFirstThunk (the ILT); the bound pointers live in FirstThunk (the IAT).
                // Bound imports leave OriginalFirstThunk 0, in which case FirstThunk still holds the names pre-bind.
                long iltBase = baseAddr + (originalFirstThunk != 0 ? originalFirstThunk : firstThunk);
                long iatBase = baseAddr + firstThunk;

                for (int i = 0; ; i++)
                {
                    long ilt = Marshal.ReadInt64(new IntPtr(iltBase + (i * 8)));
                    if (ilt == 0) break;

                    // High bit set = imported by ordinal, no name to match.
                    if ((ulong)ilt >= 0x8000000000000000UL) continue;

                    // IMAGE_IMPORT_BY_NAME: Hint(2) + Name(ASCIIZ)
                    string? impName = Marshal.PtrToStringAnsi(new IntPtr(baseAddr + ilt + 2));
                    if (impName == function)
                        return new IntPtr(iatBase + (i * 8));
                }
            }

            return IntPtr.Zero;
        }
    }
}
