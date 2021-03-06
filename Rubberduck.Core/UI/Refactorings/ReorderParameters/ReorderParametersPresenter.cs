﻿using System.Collections.ObjectModel;
using System.Linq;
using Rubberduck.Resources;
using Rubberduck.Interaction;
using Rubberduck.UI.Refactorings;
using Rubberduck.UI.Refactorings.ReorderParameters;
using System.Windows.Forms;

namespace Rubberduck.Refactorings.ReorderParameters
{
    // FIXME investigate generic IRefactoringPresenter<ReorderParametersModel> usage!
    public class ReorderParametersPresenter : IReorderParametersPresenter
    {
        private readonly IRefactoringDialog<ReorderParametersViewModel> _view;
        private readonly ReorderParametersModel _model;
        private readonly IMessageBox _messageBox;

        public ReorderParametersPresenter(IRefactoringDialog<ReorderParametersViewModel> view, ReorderParametersModel model, IMessageBox messageBox)
        {
            _view = view;
            _model = model;
            _messageBox = messageBox;
        }

        public ReorderParametersModel Show()
        {
            if (_model.TargetDeclaration == null) { return null; }

            if (_model.Parameters.Count < 2)
            {
                var message = string.Format(RubberduckUI.ReorderPresenter_LessThanTwoParametersError, _model.TargetDeclaration.IdentifierName);
                _messageBox.NotifyWarn(message, RubberduckUI.ReorderParamsDialog_TitleText);
                return null;
            }

            _view.ViewModel.Parameters = new ObservableCollection<Parameter>(_model.Parameters);

            _view.ShowDialog();
            if (_view.DialogResult != DialogResult.OK)
            {
                return null;
            }

            _model.Parameters = _view.ViewModel.Parameters.ToList();
            return _model;
        }
    }
}
