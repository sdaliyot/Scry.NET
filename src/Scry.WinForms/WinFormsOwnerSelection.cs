using System.Collections;
using System.Windows.Forms;

namespace Scry.WinForms;

/// <summary>
/// The single implementation of "which open form is the marshal owner", shared between initial
/// wiring (<c>DesktopAdapterWiring.ApplyWinForms</c>, reached via reflection since that payload
/// assembly cannot reference <c>System.Windows.Forms</c> directly) and <see cref="WinFormsDispatcher"/>'s
/// own recovery when its current owner becomes invalid.
/// <para>
/// <c>Application.OpenForms</c> is ordered by creation, not by which form is the application's
/// "main" one - a hidden utility/listener form (a third-party UI library's own internal window,
/// created before the application's real main form and left permanently invisible, is a real case
/// this was written against) can land first and never receive genuine UI interaction. Prefer the
/// first <em>visible</em> open form; fall back to the first form of any visibility only when none
/// are visible yet (e.g. before the main form has shown).
/// </para>
/// </summary>
public static class WinFormsOwnerSelection
{
    /// <summary>
    /// Takes a non-generic <see cref="IEnumerable"/> - typically <c>Application.OpenForms</c>
    /// itself, a <c>FormCollection</c> which predates generic collections - rather than
    /// <c>IEnumerable&lt;Form&gt;</c>, so a reflection caller that only has the raw property value
    /// (and no compile-time reference to <see cref="Form"/>) can invoke this directly.
    /// </summary>
    public static Form? SelectOwner(IEnumerable openForms)
    {
        if (openForms is null)
        {
            throw new ArgumentNullException(nameof(openForms));
        }

        var forms = openForms.Cast<Form>().ToArray();
        return forms.FirstOrDefault(form => form.Visible) ?? forms.FirstOrDefault();
    }
}
