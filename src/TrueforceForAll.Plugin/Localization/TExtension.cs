// {loc:T Key} for XAML, with xmlns:loc="clr-namespace:TrueforceForAll.Plugin.Localization".
// Produces a one-way Binding to Loc.Instance["Key"], so a language change or
// a translator's saved file re-renders every bound label live, and the
// English text leaves the attribute: only the key stays in the XAML.
//
// Where the extension sits decides what it returns. On a dependency property
// of a real element the Binding is applied through its own ProvideValue. In a
// Setter or a template, where WPF has no target yet, the Binding object itself
// is returned and WPF applies it per instance later. On a plain CLR property
// of a real element the resolved text is returned, because the XAML parser
// throws on a Binding there. Before Loc.Initialize (the designer, a test
// harness) the key is returned as plain text. It never throws: a broken
// label is better than a panel that fails to load.
//
// Design and phases: docs/localization-plan.md.

using System;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace TrueforceForAll.Plugin.Localization
{
    [MarkupExtensionReturnType(typeof(object))]
    public class TExtension : MarkupExtension
    {
        public TExtension() { }

        public TExtension(string key)
        {
            Key = key;
        }

        /// <summary>The key in the language table, as en.json spells it.</summary>
        [ConstructorArgument("key")]
        public string Key { get; set; }

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            string key = Key ?? string.Empty;
            try
            {
                var store = Loc.Instance;
                if (store == null) return key;

                var binding = new Binding("[" + key + "]")
                {
                    Source = store,
                    Mode = BindingMode.OneWay,
                };

                var target = serviceProvider?.GetService(typeof(IProvideValueTarget)) as IProvideValueTarget;
                if (target != null
                    && target.TargetObject is DependencyObject
                    && target.TargetProperty is DependencyProperty)
                {
                    return binding.ProvideValue(serviceProvider);
                }

                // A plain CLR property on a real element (one with no
                // DependencyProperty behind it, such as a string property a
                // code-behind helper class declares): a Binding assigned to a
                // CLR property makes the XAML parser throw and kills the whole
                // panel, so the resolved text goes in as a value. Such a label
                // does not follow a language change; the panel picks it up
                // when it is rebuilt.
                if (target != null
                    && target.TargetObject is DependencyObject
                    && !(target.TargetProperty is DependencyProperty))
                {
                    return store[key];
                }

                // A Setter or a template's shared placeholder: hand WPF the
                // Binding and let it attach when it has a target.
                return binding;
            }
            catch
            {
                return key;
            }
        }
    }
}
