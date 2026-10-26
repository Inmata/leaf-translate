using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Leaf
{
    public static class CopyInput
    {
        // The simulated copy: Ctrl down, C down, C up, Ctrl up, one key event per call. The
        // reason is ownership, not timing: each event is injected on its own so that when
        // Windows refuses one, exactly the keys this call pressed are known and can be lifted
        // again. A copy that could not be sent is therefore cleaned up by this program without
        // ever touching a key the user was holding, and nothing about the copy is retried.
        //
        // Ownership is decided before each press, not by the fact that the press succeeded: a
        // successful down only proves the event was delivered, and for a key the user was
        // already holding it would still be the user's key. So a key the desktop already holds
        // is never pressed as this copy's own, and a key this copy did not press is never
        // lifted - otherwise a copy that pressed C over a held C and then failed could lift the
        // user's own C away. A key already down refuses the copy before anything is sent.
        //
        // This check reads the key state at the instant before each press, and that is the
        // whole boundary it can promise: a key that comes down after this read and before
        // Windows delivers the press is a real race this code cannot close, so it claims only
        // that a key observed held is not pressed, never that no key can ever be lost.
        //
        // No inter-event delay is inserted. The order Ctrl, C then release is what matters, and
        // that order is already the call order, so a pause would only add a timing assumption
        // this code has no evidence for. An intermittent "clipboard did not change" is not
        // claimed here to be a timing problem, and the injection does not try to address one.
        private static readonly ushort[] CopyStrokes = { 17, 67, 67, 17 };
        private static readonly bool[] CopyStrokeUps = { false, false, true, true };
        public static CopyInjection InjectCopy(IKeyInjector injector)
        {
            var copy = new CopyInjection();
            if (injector == null) return copy;
            // The keys this call pressed down and has not lifted yet, oldest first.
            var pressed = new List<ushort>();
            for (int stroke = 0; stroke < CopyStrokes.Length; stroke++) {
                int stepError;
                bool accepted;
                // A press is refused outright when the desktop already holds the key. Since the
                // copy pressed nothing for it, nothing of the user's is lifted for it either;
                // only keys this call really pressed are handed to Lift.
                if (!CopyStrokeUps[stroke] && !Owned(injector, CopyStrokes[stroke], ref copy)) {
                    Lift(injector, pressed, ref copy);
                    return copy;
                }
                // An event that throws is a refused event too: the copy stops and is cleaned up
                // exactly as for one Windows refused outright.
                try { accepted = injector.Inject(CopyStrokes[stroke], CopyStrokeUps[stroke], out stepError); }
                catch (Exception failure) { accepted = false; stepError = failure.HResult; }
                if (!accepted) {
                    copy.Error = stepError;
                    Lift(injector, pressed, ref copy);
                    return copy;
                }
                if (CopyStrokeUps[stroke]) pressed.Remove(CopyStrokes[stroke]);
                else pressed.Add(CopyStrokes[stroke]);
            }
            copy.Sent = true;
            return copy;
        }
        // Whether this copy may press a key: only when the desktop does not already hold it. A
        // key state that could not be read is treated as held, so an unknown state never
        // presses a key that might belong to the user. No Win32 error is set for a refusal on
        // a held key - nothing Windows refused happened; the caller reports its own outcome.
        private static bool Owned(IKeyInjector injector, ushort key, ref CopyInjection copy)
        {
            try { return !injector.IsDown(key); }
            catch (Exception failure) { copy.Error = failure.HResult; return false; }
        }
        // Undoes only what this call pressed, newest first, so a modified key is lifted before
        // its modifier. A key the sequence already released is no longer in the list, and a key
        // this call never pressed is never touched at all. A lift Windows refused, or that threw,
        // is recorded with its error: the copy reports that it could not be sure the key came up
        // rather than silently pretending it did.
        private static void Lift(IKeyInjector injector, List<ushort> pressed, ref CopyInjection copy)
        {
            for (int i = pressed.Count - 1; i >= 0; i--) {
                int error = 0;
                bool lifted;
                try { lifted = injector.Inject(pressed[i], true, out error); }
                catch (Exception failure) { lifted = false; error = failure.HResult; }
                if (!lifted) { copy.CleanupFailed = true; copy.CleanupError = error; }
            }
        }
    }
}
