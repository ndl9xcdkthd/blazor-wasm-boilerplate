using FSH.BlazorWebAssembly.Client.Infrastructure.ApiClient;
using FSH.BlazorWebAssembly.Client.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using MudBlazor;

namespace FSH.BlazorWebAssembly.Client.Components.EntityManager;

public partial class EntityManager<TEntity>
    where TEntity : class, new()
{
    [Parameter]
    [EditorRequired]
    public EntityManagerContext<TEntity> Context { get; set; } = default!;

    [Parameter]
    public bool Dense { get; set; }
    [Parameter]
    public bool Striped { get; set; } = true;
    [Parameter]
    public bool Bordered { get; set; }
    [Parameter]
    public bool Loading { get; set; }

    [Parameter]
    public string? SearchString { get; set; }
    [Parameter]
    public EventCallback<string> SearchStringChanged { get; set; }

    [Parameter]
    public RenderFragment? AdvancedSearchContent { get; set; }

    [Parameter]
    public RenderFragment<TEntity>? ActionsContent { get; set; }
    [Parameter]
    public RenderFragment<TEntity>? ExtraActions { get; set; }
    [Parameter]
    public RenderFragment<TEntity>? ChildRowContent { get; set; }

    [Parameter]
    public RenderFragment<TEntity>? EditFormContent { get; set; }

    [CascadingParameter]
    protected Task<AuthenticationState> AuthState { get; set; } = default!;
    [Inject]
    protected IAuthorizationService AuthService { get; set; } = default!;

    private bool _canSearch;
    private bool _canCreate;
    private bool _canUpdate;
    private bool _canDelete;

    private MudTable<TEntity>? _table;
    private IEnumerable<TEntity>? _entityList;
    private int _totalItems;

    protected override async Task OnInitializedAsync()
    {
        var state = await AuthState;
        _canSearch = await CanDoPermission(Context.SearchPermission, state);
        _canCreate = await CanDoPermission(Context.CreatePermission, state);
        _canUpdate = await CanDoPermission(Context.UpdatePermission, state);
        _canDelete = await CanDoPermission(Context.DeletePermission, state);

        await LoadDataAsync();
    }

    private async Task<bool> CanDoPermission(string? permission, AuthenticationState state) =>
        !string.IsNullOrWhiteSpace(permission) &&
            ((bool.TryParse(permission, out bool can) && can) || // check if permmission equals "True", then it's allowed
            (await AuthService.AuthorizeAsync(state.User, permission)).Succeeded);

    private bool HasActions => _canUpdate || _canDelete || Context.HasExtraActionsFunc is null || Context.HasExtraActionsFunc();

    // Client side paging/filtering
    private bool LocalSearch(TEntity entity)
    {
        if (Context is not ClientEntityManagerContext<TEntity> clientContext ||
            clientContext.SearchFunc is null)
        {
            return string.IsNullOrWhiteSpace(SearchString);
        }

        return clientContext.SearchFunc(SearchString, entity);
    }

    private async Task LoadDataAsync()
    {
        if (Loading || Context is not ClientEntityManagerContext<TEntity> clientContext)
        {
            return;
        }

        Loading = true;

        if (await ApiHelper.ExecuteCallGuardedAsync(() => clientContext.LoadDataFunc(), _snackBar) is ListResult<TEntity> result)
        {
            _entityList = result.Data;
        }

        Loading = false;
    }

    // Server Side paging/filtering
    private async Task OnSearch(string? text = null)
    {
        await SearchStringChanged.InvokeAsync(SearchString);

        if (Context is ServerEntityManagerContext<TEntity>)
        {
            _table?.ReloadServerData();
        }
    }

    private Func<TableState, Task<TableData<TEntity>>>? ServerReloadFunc =>
        Context is ServerEntityManagerContext<TEntity> ? ServerReload : null;

    private async Task<TableData<TEntity>> ServerReload(TableState state)
    {
        if (!string.IsNullOrWhiteSpace(SearchString))
        {
            state.Page = 0;
        }

        await LoadDataAsync(state);

        return new TableData<TEntity> { TotalItems = _totalItems, Items = _entityList };
    }

    private async Task LoadDataAsync(TableState state)
    {
        if (Loading || Context is not ServerEntityManagerContext<TEntity> serverContext)
        {
            return;
        }

        Loading = true;

        string[]? orderings = null;
        if (!string.IsNullOrEmpty(state.SortLabel))
        {
            orderings = state.SortDirection == SortDirection.None
                ? new[] { $"{state.SortLabel}" }
                : new[] { $"{state.SortLabel} {state.SortDirection}" };
        }

        var filter = new PaginationFilter
        {
            PageSize = state.PageSize,
            PageNumber = state.Page + 1,
            Keyword = SearchString,
            OrderBy = orderings ?? Array.Empty<string>()
        };

        var result = await serverContext.SearchFunc(filter);
        if (result.Succeeded)
        {
            _totalItems = result.TotalCount;
            _entityList = result.Data;
        }

        Loading = false;
    }

    private async Task InvokeModal(object? id = default)
    {
        _ = Context.IdFunc ?? throw new InvalidOperationException("IdFunc can't be null!");

        var parameters = new DialogParameters()
        {
            { nameof(AddEditModal<TEntity>.Context), Context },
            { nameof(AddEditModal<TEntity>.EditFormContent), EditFormContent },
            { nameof(AddEditModal<TEntity>.IsCreate), id == default },
            { nameof(AddEditModal<TEntity>.Id), id }
        };

        if (id != default)
        {
            var entity = _entityList?.FirstOrDefault(c => Context.IdFunc(c) == id);
            if (entity is not null)
            {
                parameters.Add(nameof(AddEditModal<TEntity>.EntityModel), entity);
            }
        }

        var options = new DialogOptions { CloseButton = true, MaxWidth = MaxWidth.Medium, FullWidth = true, DisableBackdropClick = true };
        var dialog = _dialogService.Show<AddEditModal<TEntity>>(id == default ? L["Create"] : L["Edit"], parameters, options);
        var result = await dialog.Result;
        if (!result.Cancelled)
        {
            await ResetAsync();
        }
    }

    private async Task Delete(object? id)
    {
        _ = Context.DeleteFunc ?? throw new InvalidOperationException("CreateFunc can't be null!");

        string deleteContent = L["You're sure you want to delete {0} with id '{1}'?"];
        var parameters = new DialogParameters
        {
            { nameof(Shared.Dialogs.DeleteConfirmation.ContentText), string.Format(deleteContent, Context.EntityName, id) }
        };
        var options = new DialogOptions { CloseButton = true, MaxWidth = MaxWidth.Small, FullWidth = true, DisableBackdropClick = true };
        var dialog = _dialogService.Show<Shared.Dialogs.DeleteConfirmation>(L["Delete"], parameters, options);
        var result = await dialog.Result;
        if (!result.Cancelled)
        {
            await ApiHelper.ExecuteCallGuardedAsync(
                () => Context.DeleteFunc(id),
                _snackBar);

            await ResetAsync();
        }
    }

    private Task ResetAsync()
    {
        if (Context is ClientEntityManagerContext<TEntity>)
        {
            return LoadDataAsync();
        }
        else
        {
            SearchString = string.Empty;
            return OnSearch();
        }
    }
}