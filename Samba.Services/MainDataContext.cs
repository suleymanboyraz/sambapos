﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Samba.Domain;
using Samba.Domain.Models.Accounts;
using Samba.Domain.Models.Actions;
using Samba.Domain.Models.Menus;
using Samba.Domain.Models.Settings;
using Samba.Domain.Models.Tables;
using Samba.Domain.Models.Tickets;
using Samba.Domain.Models.Users;
using Samba.Infrastructure.Data;
using Samba.Infrastructure.Data.Serializer;
using Samba.Localization.Properties;
using Samba.Persistance.Data;

namespace Samba.Services
{
    public class TicketCommitResult
    {
        public int TicketId { get; set; }
        public string ErrorMessage { get; set; }
    }

    public class MainDataContext
    {
        private class TicketWorkspace
        {
            private IWorkspace _workspace;
            public Ticket Ticket { get; private set; }

            public void CreateTicket(Department department)
            {
                Debug.Assert(_workspace == null);
                Debug.Assert(Ticket == null);
                Debug.Assert(department != null);

                _workspace = WorkspaceFactory.Create();
                Ticket = Ticket.Create(department);
            }

            public void OpenTicket(int ticketId)
            {
                Debug.Assert(_workspace == null);
                Debug.Assert(Ticket == null);
                _workspace = WorkspaceFactory.Create();
                Ticket = _workspace.Single<Ticket>(ticket => ticket.Id == ticketId);
            }

            public void CommitChanges()
            {
                Debug.Assert(_workspace != null);
                Debug.Assert(Ticket != null);
                Debug.Assert(Ticket.Id > 0 || Ticket.TicketItems.Count > 0);
                if (Ticket.Id == 0 && Ticket.TicketNumber != null)
                    _workspace.Add(Ticket);
                Ticket.LastUpdateTime = DateTime.Now;
                _workspace.CommitChanges();
            }

            public void Reset()
            {
                Debug.Assert(Ticket != null);
                Debug.Assert(_workspace != null);
                Ticket = null;
                _workspace = null;
            }

            public Table LoadTable(string locationName)
            {
                return _workspace.Single<Table>(x => x.Name == locationName);
            }

            public Account UpdateAccount(Account account)
            {
                if (account == Account.Null)
                    return Account.Null;

                if (account.Id == 0)
                {
                    using (var workspace = WorkspaceFactory.Create())
                    {
                        workspace.Add(account);
                        workspace.CommitChanges();
                    }
                    return account;
                }

                var result = _workspace.Single<Account>(
                        x => x.Id == account.Id
                        && x.Name == account.Name
                        && x.Address == account.Address
                        && x.PhoneNumber == account.PhoneNumber
                        && x.Note == account.Note);

                if (result == null)
                {
                    result = _workspace.Single<Account>(x => x.Id == account.Id);
                    Debug.Assert(result != null);
                    result.Address = account.Address;
                    result.Name = account.Name;
                    result.PhoneNumber = account.PhoneNumber;
                    result.Note = account.Note;
                }
                return result;
            }

            public Table GetTableWithId(int tableId)
            {
                return _workspace.Single<Table>(x => x.Id == tableId);
            }

            public Table GetTicketTable()
            {
                Debug.Assert(!string.IsNullOrEmpty(Ticket.LocationName));
                Debug.Assert(Ticket != null);
                return _workspace.Single<Table>(x => x.Name == Ticket.LocationName);
            }

            public void ResetTableData(IEntity ticket)
            {
                _workspace.All<Table>(x => x.TicketId == ticket.Id).ToList().ForEach(x => x.Reset());
            }

            public void RemoveTicketItems(IEnumerable<TicketItem> selectedItems)
            {
                selectedItems.ToList().ForEach(x => Ticket.TicketItems.Remove(x));
                selectedItems.Where(x => x.Id > 0).ToList().ForEach(x => _workspace.Delete(x));
            }

            public void RemoveServices(IEnumerable<Service> services)
            {
                services.ToList().ForEach(x => _workspace.Delete(x));
            }

            public void AddItemToSelectedTicket(TicketItem model)
            {
                _workspace.Add(model);
            }

        }

        public int AccountCount { get; set; }
        public int TableCount { get; set; }
        public string NumeratorValue { get; set; }

        private IWorkspace _tableWorkspace;
        private readonly TicketWorkspace _ticketWorkspace = new TicketWorkspace();

        private IEnumerable<AppRule> _rules;
        public IEnumerable<AppRule> Rules { get { return _rules ?? (_rules = Dao.Query<AppRule>(x => x.Actions)); } }

        private IEnumerable<AppAction> _actions;
        public IEnumerable<AppAction> Actions { get { return _actions ?? (_actions = Dao.Query<AppAction>()); } }

        private IEnumerable<TableScreen> _tableScreens;
        public IEnumerable<TableScreen> TableScreens { get { return _tableScreens ?? (_tableScreens = Dao.Query<TableScreen>(x => x.Tables)); } }

        private IEnumerable<Department> _departments;
        public IEnumerable<Department> Departments
        {
            get
            {
                return _departments ?? (_departments = Dao.Query<Department>(x => x.TicketNumerator, x => x.OrderNumerator,
                    x => x.ServiceTemplates, x => x.TicketTagGroups.Select(y => y.Numerator), x => x.TicketTagGroups.Select(y => y.TicketTags)));
            }
        }

        private IEnumerable<Department> _permittedDepartments;
        public IEnumerable<Department> PermittedDepartments
        {
            get
            {
                return _permittedDepartments ?? (
                    _permittedDepartments = Departments.Where(
                      x => AppServices.IsUserPermittedFor(PermissionNames.UseDepartment + x.Id)));
            }
        }

        private IDictionary<int, Reason> _reasons;
        public IDictionary<int, Reason> Reasons { get { return _reasons ?? (_reasons = Dao.BuildDictionary<Reason>()); } }

        private IEnumerable<WorkPeriod> _lastTwoWorkPeriods;
        public IEnumerable<WorkPeriod> LastTwoWorkPeriods
        {
            get { return _lastTwoWorkPeriods ?? (_lastTwoWorkPeriods = GetLastTwoWorkPeriods()); }
        }

        private IEnumerable<User> _users;
        public IEnumerable<User> Users { get { return _users ?? (_users = Dao.Query<User>(x => x.UserRole)); } }

        private IEnumerable<TaxTemplate> _taxTemplates;
        public IEnumerable<TaxTemplate> TaxTemplates
        {
            get { return _taxTemplates ?? (_taxTemplates = Dao.Query<TaxTemplate>()); }
        }

        private IEnumerable<ServiceTemplate> _serviceTemplates;
        public IEnumerable<ServiceTemplate> ServiceTemplates
        {
            get { return _serviceTemplates ?? (_serviceTemplates = Dao.Query<ServiceTemplate>()); }
        }

        public WorkPeriod CurrentWorkPeriod { get { return LastTwoWorkPeriods.LastOrDefault(); } }
        public WorkPeriod PreviousWorkPeriod { get { return LastTwoWorkPeriods.Count() > 1 ? LastTwoWorkPeriods.FirstOrDefault() : null; } }

        public TableScreen SelectedTableScreen { get; set; }
        public Ticket SelectedTicket { get { return _ticketWorkspace.Ticket; } }

        private Department _selectedDepartment;
        public Department SelectedDepartment
        {
            get { return _selectedDepartment; }
            set
            {
                if (value != null && (_selectedDepartment == null || _selectedDepartment.Id != value.Id))
                {
                    SelectedTableScreen = TableScreens.FirstOrDefault(x => x.Id == value.TableScreenId);
                }
                _selectedDepartment = value;
            }
        }

        public bool IsCurrentWorkPeriodOpen
        {
            get
            {
                return CurrentWorkPeriod != null &&
                 CurrentWorkPeriod.StartDate == CurrentWorkPeriod.EndDate;
            }
        }

        public MainDataContext()
        {
            _ticketWorkspace = new TicketWorkspace();
        }

        private static IEnumerable<WorkPeriod> GetLastTwoWorkPeriods()
        {
            return Dao.Last<WorkPeriod>(2);
        }

        public void ResetUserData()
        {
            _permittedDepartments = null;
            ThreadPool.QueueUserWorkItem(ResetTableAndAccountCounts);
        }

        private void ResetTableAndAccountCounts(object state)
        {
            AccountCount = Dao.Count<Account>(null);
            TableCount = Dao.Count<Table>(null);
        }

        public void StartWorkPeriod(string description, decimal cashAmount, decimal creditCardAmount, decimal ticketAmount)
        {
            using (var workspace = WorkspaceFactory.Create())
            {
                _lastTwoWorkPeriods = null;

                var latestWorkPeriod = workspace.Last<WorkPeriod>();
                if (latestWorkPeriod != null && latestWorkPeriod.StartDate == latestWorkPeriod.EndDate)
                {
                    return;
                }

                var now = DateTime.Now;
                var newPeriod = new WorkPeriod
                                    {
                                        StartDate = now,
                                        EndDate = now,
                                        StartDescription = description,
                                        CashAmount = cashAmount,
                                        CreditCardAmount = creditCardAmount,
                                        TicketAmount = ticketAmount
                                    };

                workspace.Add(newPeriod);
                workspace.CommitChanges();
                _lastTwoWorkPeriods = null;
            }
        }

        public void StopWorkPeriod(string description)
        {
            using (var workspace = WorkspaceFactory.Create())
            {
                var period = workspace.Last<WorkPeriod>();
                if (period.EndDate == period.StartDate)
                {
                    period.EndDate = DateTime.Now;
                    period.EndDescription = description;
                    workspace.CommitChanges();
                }
                _lastTwoWorkPeriods = null;
            }
        }

        public string GetReason(int reasonId)
        {
            return Reasons.ContainsKey(reasonId) ? Reasons[reasonId].Name : Resources.UndefinedWithBrackets;
        }

        public void UpdateTicketTable(Ticket ticket)
        {
            if (string.IsNullOrEmpty(ticket.LocationName)) return;
            var table = _ticketWorkspace.LoadTable(ticket.LocationName);
            if (table != null)
            {
                if (ticket.IsPaid || ticket.TicketItems.Count == 0)
                {
                    if (table.TicketId == ticket.Id)
                    {
                        table.TicketId = 0;
                        table.IsTicketLocked = false;
                    }
                }
                else
                {
                    table.TicketId = ticket.Id;
                    table.IsTicketLocked = ticket.Locked;
                }
            }
            else ticket.LocationName = "";
        }

        public void UpdateTableData(TableScreen selectedTableScreen, int pageNo)
        {
            var set = selectedTableScreen.Tables.Select(x => x.Id);
            if (selectedTableScreen.PageCount > 1)
            {
                set = selectedTableScreen.Tables
                    .OrderBy(x => x.Order)
                    .Skip(pageNo * selectedTableScreen.ItemCountPerPage)
                    .Take(selectedTableScreen.ItemCountPerPage)
                    .Select(x => x.Id);
            }

            var result = Dao.Select<Table, dynamic>(x => new { x.Id, Tid = x.TicketId, Locked = x.IsTicketLocked },
                                                   x => set.Contains(x.Id));
            foreach (var td in result)
            {
                var tid = td.Id;
                var table = selectedTableScreen.Tables.Single(x => x.Id == tid);
                table.TicketId = td.Tid;
                table.IsTicketLocked = td.Locked;
            }
        }

        public void AssignAccountToTicket(Ticket ticket, Account account)
        {
            Debug.Assert(ticket != null);
            ticket.UpdateAccount(_ticketWorkspace.UpdateAccount(account));
        }

        public void AssignAccountToSelectedTicket(Account account)
        {
            if (SelectedTicket == null)
            {
                _ticketWorkspace.CreateTicket(SelectedDepartment);
            }
            AssignAccountToTicket(SelectedTicket, account);
        }

        public void AssignLocationToSelectedTicket(int locationId)
        {
            if (SelectedTicket == null)
            {
                _ticketWorkspace.CreateTicket(SelectedDepartment);
            }

            var table = _ticketWorkspace.GetTableWithId(locationId);

            Debug.Assert(SelectedTicket != null);

            if (!string.IsNullOrEmpty(SelectedTicket.LocationName))
            {
                var oldTable = _ticketWorkspace.GetTicketTable();
                if (oldTable.TicketId == SelectedTicket.Id)
                {
                    oldTable.IsTicketLocked = false;
                    oldTable.TicketId = 0;
                }
            }

            if (table.TicketId > 0 && table.TicketId != SelectedTicket.Id)
            {
                MoveTicketItems(SelectedTicket.TicketItems.ToList(), table.TicketId);
                OpenTicket(table.TicketId);
            }

            SelectedTicket.LocationName = table.Name;
            if (SelectedDepartment != null) SelectedTicket.DepartmentId = SelectedDepartment.Id;
            table.TicketId = SelectedTicket.GetRemainingAmount() > 0 ? SelectedTicket.Id : 0;
        }

        public void UpdateTables(int tableScreenId, int pageNo)
        {
            SelectedTableScreen = null;
            if (tableScreenId > 0)
            {
                SelectedTableScreen = TableScreens.Single(x => x.Id == tableScreenId);
                AppServices.MainDataContext.UpdateTableData(SelectedTableScreen, pageNo);
            }
        }

        public void OpenTicket(int ticketId)
        {
            _ticketWorkspace.OpenTicket(ticketId);
        }

        public TicketCommitResult CloseTicket()
        {
            var result = new TicketCommitResult();
            Debug.Assert(SelectedTicket != null);
            var changed = false;
            if (SelectedTicket.Id > 0)
            {
                var lup = Dao.Single<Ticket, DateTime>(SelectedTicket.Id, x => x.LastUpdateTime);
                if (SelectedTicket.LastUpdateTime.CompareTo(lup) != 0)
                {
                    var currentTicket = Dao.Single<Ticket>(x => x.Id == SelectedTicket.Id, x => x.TicketItems, x => x.Payments);
                    if (currentTicket.LocationName != SelectedTicket.LocationName)
                    {
                        result.ErrorMessage = string.Format(Resources.TicketMovedRetryLastOperation_f, currentTicket.LocationName);
                        changed = true;
                    }

                    if (currentTicket.IsPaid != SelectedTicket.IsPaid)
                    {
                        if (currentTicket.IsPaid)
                        {
                            result.ErrorMessage = Resources.TicketPaidChangesNotSaved;
                        }
                        if (SelectedTicket.IsPaid)
                        {
                            result.ErrorMessage = Resources.TicketChangedRetryLastOperation;
                        }
                        changed = true;
                    }
                    else if (currentTicket.LastPaymentDate != SelectedTicket.LastPaymentDate)
                    {
                        var currentPaymentIds = SelectedTicket.Payments.Select(x => x.Id).Distinct();
                        var unknownPayments = currentTicket.Payments.Where(x => !currentPaymentIds.Contains(x.Id)).FirstOrDefault();
                        if (unknownPayments != null)
                        {
                            result.ErrorMessage = Resources.TicketPaidLastChangesNotSaved;
                            changed = true;
                        }
                    }
                }
            }

            if (!string.IsNullOrEmpty(SelectedTicket.LocationName) && SelectedTicket.Id == 0)
            {
                var ticketId = Dao.Select<Table, int>(x => x.TicketId, x => x.Name == SelectedTicket.LocationName).FirstOrDefault();
                {
                    if (ticketId > 0)
                    {
                        result.ErrorMessage = string.Format(Resources.TableChangedRetryLastOperation_f, SelectedTicket.LocationName);
                        changed = true;
                    }
                }
            }

            var canSumbitTicket = !changed && SelectedTicket.CanSubmit; // Fişi kaydedebilmek için gün sonu yapılmamış ve fişin ödenmemiş olması gerekir.

            if (canSumbitTicket)
            {
                _ticketWorkspace.RemoveTicketItems(SelectedTicket.PopRemovedTicketItems());
                _ticketWorkspace.RemoveServices(SelectedTicket.PopRemovedServices());
                Recalculate(SelectedTicket);
                SelectedTicket.IsPaid = SelectedTicket.RemainingAmount == 0;

                if (SelectedTicket.TicketItems.Count > 0)
                {
                    if (SelectedTicket.TicketItems.Where(x => !x.Locked).FirstOrDefault() != null)
                    {
                        SelectedTicket.MergeLinesAndUpdateOrderNumbers(NumberGenerator.GetNextNumber(SelectedDepartment.OrderNumerator.Id));
                        SelectedTicket.TicketItems.Where(x => x.Id == 0).ToList().ForEach(x => x.CreatedDateTime = DateTime.Now);
                    }

                    if (SelectedTicket.Id == 0)
                    {
                        UpdateTicketNumber(SelectedTicket);
                        SelectedTicket.LastOrderDate = DateTime.Now;
                        _ticketWorkspace.CommitChanges();
                    }

                    Debug.Assert(!string.IsNullOrEmpty(SelectedTicket.TicketNumber));
                    Debug.Assert(SelectedTicket.Id > 0);

                    //Otomatik yazdırma
                    AppServices.PrintService.AutoPrintTicket(SelectedTicket);
                    SelectedTicket.LockTicket();
                }

                UpdateTicketTable(SelectedTicket);
                if (SelectedTicket.Id > 0)  // eğer adisyonda satır yoksa ID burada 0 olmalı.
                    _ticketWorkspace.CommitChanges();
                Debug.Assert(SelectedTicket.TicketItems.Count(x => x.OrderNumber == 0) == 0);
            }
            result.TicketId = SelectedTicket.Id;
            _ticketWorkspace.Reset();

            return result;
        }

        public void UpdateTicketNumber(Ticket ticket)
        {
            UpdateTicketNumber(ticket, SelectedDepartment.TicketNumerator);
        }

        public void UpdateTicketNumber(Ticket ticket, Numerator numerator)
        {
            if (numerator == null) numerator = SelectedDepartment.TicketNumerator;
            if (string.IsNullOrEmpty(ticket.TicketNumber))
                ticket.TicketNumber = NumberGenerator.GetNextString(numerator.Id);
        }

        public void AddPaymentToSelectedTicket(decimal tenderedAmount, DateTime date, PaymentType paymentType)
        {
            SelectedTicket.AddPayment(date, tenderedAmount, paymentType, AppServices.CurrentLoggedInUser.Id);
        }

        public void PaySelectedTicket(PaymentType paymentType)
        {
            AddPaymentToSelectedTicket(SelectedTicket.GetRemainingAmount(), DateTime.Now, paymentType);
        }

        public IList<Table> LoadTables(string selectedTableScreen)
        {
            if (_tableWorkspace != null)
            {
                _tableWorkspace.CommitChanges();
            }
            _tableWorkspace = WorkspaceFactory.Create();
            return _tableWorkspace.Single<TableScreen>(x => x.Name == selectedTableScreen).Tables;
        }

        public void SaveTables()
        {
            if (_tableWorkspace != null)
            {
                _tableWorkspace.CommitChanges();
                _tableWorkspace = null;
                _tableScreens = null;
            }
        }

        public void ResetCache()
        {
            Debug.Assert(_ticketWorkspace.Ticket == null);

            if (_tableWorkspace == null)
            {
                var selectedDepartment = SelectedDepartment != null ? SelectedDepartment.Id : 0;
                var selectedTableScreen = SelectedTableScreen != null ? SelectedTableScreen.Id : 0;

                SelectedTableScreen = null;
                SelectedDepartment = null;

                _tableScreens = null;
                _departments = null;
                _permittedDepartments = null;
                _reasons = null;
                _lastTwoWorkPeriods = null;
                _users = null;
                _rules = null;
                _actions = null;
                _taxTemplates = null;
                _serviceTemplates = null;

                if (selectedTableScreen > 0 && TableScreens.Count(x => x.Id == selectedTableScreen) > 0)
                    SelectedTableScreen = TableScreens.Single(x => x.Id == selectedTableScreen);
                if (selectedDepartment > 0 && Departments.Count(x => x.Id == selectedDepartment) > 0)
                    SelectedDepartment = Departments.Single(x => x.Id == selectedDepartment);
            }
        }

        public void OpenTicketFromTableName(string tableName)
        {
            var table = Dao.SingleWithCache<Table>(x => x.Name == tableName);
            if (table != null)
            {
                if (table.TicketId > 0)
                    OpenTicket(table.TicketId);
                AssignLocationToSelectedTicket(table.Id);
            }
        }

        public void OpenTicketFromTicketNumber(string ticketNumber)
        {
            Debug.Assert(_ticketWorkspace.Ticket == null);
            var id = Dao.Select<Ticket, int>(x => x.Id, x => x.TicketNumber == ticketNumber).FirstOrDefault();
            if (id > 0) OpenTicket(id);
        }

        public string GetUserName(int userId)
        {
            return userId > 0 ? Users.Single(x => x.Id == userId).Name : "-";
        }

        public void CreateNewTicket()
        {
            _ticketWorkspace.CreateTicket(SelectedDepartment);
        }

        public TicketCommitResult MoveTicketItems(IEnumerable<TicketItem> selectedItems, int targetTicketId)
        {
            var clonedItems = selectedItems.Select(ObjectCloner.Clone).ToList();

            _ticketWorkspace.RemoveTicketItems(selectedItems);

            if (SelectedTicket.TicketItems.Count == 0)
            {
                var info = targetTicketId.ToString();
                if (targetTicketId > 0)
                {
                    var tData = Dao.Single<Ticket, dynamic>(targetTicketId, x => new { x.LocationName, x.TicketNumber });
                    info = tData.LocationName + " - " + tData.TicketNumber;
                }
                if (!string.IsNullOrEmpty(SelectedTicket.Note)) SelectedTicket.Note += "\r";
                SelectedTicket.Note += SelectedTicket.LocationName + " => " + info;
            }

            CloseTicket();

            if (targetTicketId == 0)
                CreateNewTicket();
            else OpenTicket(targetTicketId);

            foreach (var ticketItem in clonedItems)
            {
                SelectedTicket.TicketItems.Add(ticketItem);
            }

            SelectedTicket.LastOrderDate = DateTime.Now;
            return CloseTicket();
        }

        public void ResetTableDataForSelectedTicket()
        {
            _ticketWorkspace.ResetTableData(SelectedTicket);
            AppServices.MainDataContext.UpdateTicketTable(SelectedTicket);
            _ticketWorkspace.CommitChanges();
        }

        public void AddItemToSelectedTicket(TicketItem model)
        {
            _ticketWorkspace.AddItemToSelectedTicket(model);
        }

        public void Recalculate(Ticket ticket)
        {
            ticket.Recalculate(AppServices.SettingService.AutoRoundDiscount, AppServices.CurrentLoggedInUser.Id);
        }

        public TaxTemplate GetTaxTemplate(int menuItemId)
        {
            return AppServices.DataAccessService.GetMenuItem(menuItemId).TaxTemplate;
        }
    }
}
