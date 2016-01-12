using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Media.Imaging;
using Microsoft.Vbe.Interop;
using Rubberduck.Parsing.Symbols;
using Rubberduck.UI;
using resx = Rubberduck.UI.CodeExplorer.CodeExplorer;

namespace Rubberduck.Navigation.CodeExplorer
{
    public class CodeExplorerComponentViewModel : ViewModelBase
    {
        private readonly Declaration _declaration;
        private readonly IEnumerable<CodeExplorerMemberViewModel> _members;

        private static readonly DeclarationType[] MemberTypes =
        {
            DeclarationType.Constant, 
            DeclarationType.Enumeration, 
            DeclarationType.Event, 
            DeclarationType.Function, 
            DeclarationType.LibraryFunction, 
            DeclarationType.LibraryProcedure, 
            DeclarationType.Procedure,
            DeclarationType.PropertyGet, 
            DeclarationType.PropertyLet, 
            DeclarationType.PropertySet, 
            DeclarationType.UserDefinedType, 
            DeclarationType.Variable, 
        };

        public CodeExplorerComponentViewModel(Declaration declaration, IEnumerable<Declaration> declarations)
        {
            _declaration = declaration;
            _members = declarations.GroupBy(item => item.Scope).SelectMany(grouping =>
                            grouping.Where(item => item.ParentDeclaration != null
                                                && MemberTypes.Contains(item.DeclarationType)
                                                && item.ParentDeclaration.Equals(declaration))
                                .OrderBy(item => item.QualifiedSelection.Selection.StartLine)
                                .Select(item => new CodeExplorerMemberViewModel(item, grouping)))
                                .ToList();
            
        }

        public IEnumerable<CodeExplorerMemberViewModel> Members { get { return _members; } }

        private bool _isErrorState;
        public bool IsErrorState { get { return _isErrorState; } set { _isErrorState = value; OnPropertyChanged(); } }

        public bool IsTestModule
        {
            get
            {
                return _declaration.DeclarationType == DeclarationType.Module 
                       && _declaration.Annotations.Split('\n').Contains(Parsing.Grammar.Annotations.TestModule);
            }
        }

        public string Name { get { return _declaration.IdentifierName; } }


        private vbext_ComponentType ComponentType { get { return _declaration.QualifiedName.QualifiedModuleName.Component.Type; } }

        private static readonly IDictionary<vbext_ComponentType, DeclarationType> DeclarationTypes = new Dictionary<vbext_ComponentType, DeclarationType>
        {
            { vbext_ComponentType.vbext_ct_ClassModule, DeclarationType.Class },
            { vbext_ComponentType.vbext_ct_StdModule, DeclarationType.Module },
            { vbext_ComponentType.vbext_ct_Document, DeclarationType.Document },
            { vbext_ComponentType.vbext_ct_MSForm, DeclarationType.UserForm }
        };

        private DeclarationType DeclarationType
        {
            get
            {
                DeclarationType result;
                if (!DeclarationTypes.TryGetValue(ComponentType, out result))
                {
                    result = DeclarationType.Class;
                }

                return result;
            }
        }

        private static readonly IDictionary<DeclarationType,BitmapImage> Icons = new Dictionary<DeclarationType, BitmapImage>
        {
            { DeclarationType.Class, GetImageSource(resx.VSObject_Class) },
            { DeclarationType.Module, GetImageSource(resx.VSObject_Module) },
            { DeclarationType.UserForm, GetImageSource(resx.VSProject_form) },
            { DeclarationType.Document, GetImageSource(resx.document_office) }
        };

        public BitmapImage Icon { get { return Icons[DeclarationType]; } }
    }
}