// --------------------------------------------------------------------------------------------------------------------
// <copyright file="ResourceMgmtContext.cs" company="BIS">BIS</copyright>
// <summary>Defines the ResourceMgmtContext type.</summary>
// --------------------------------------------------------------------------------------------------------------------
namespace ResourceMgmt.Infrastructure.EF
{
    using System;
    using System.Collections.Generic;
    using System.Configuration;
    using System.Data.Entity;
    using System.Data.Entity.Core;
    using System.Data.Entity.Core.Objects;
    using System.Data.Entity.Infrastructure;
    using System.Data.Entity.ModelConfiguration;
    using System.Data.Entity.ModelConfiguration.Conventions;
    using System.Linq;
    using System.Reflection;

    using Bis.Common.EF;
    using Bis.Common.Security;

    using ResourceMgmt.Domain;
    using ResourceMgmt.Domain.Model;
    using ResourceMgmt.Infrastructure.Repository.Contract;

    /// <summary>The resource management context.</summary>
    public class ResourceMgmtContext : BaseContext, IChangeTracker
    {
        #region Properties

        /// <summary>The connection string.</summary>
        private static readonly string ConnectionString;
        
        /// <summary>The maps.</summary>
        private static readonly IEnumerable<object> EntityTypeConfigurations = Assembly.GetExecutingAssembly()
                                                                                        .GetTypes()
                                                                                        .Where(t => t.BaseType != null &&
                                                                                                    t.BaseType.IsGenericType &&
                                                                                                    t.BaseType.GetGenericTypeDefinition() == typeof(EntityTypeConfiguration<>))
                                                                                                    .Select(Activator.CreateInstance)
                                                                                        .ToList();
        
        /// <summary>The _identity context provider.</summary>
        private readonly IIdentityContextProvider identityContextProvider;
        
        /// <summary>
        /// The _subscribed trackers.
        /// </summary>
        private readonly List<TrackedData> subscribedTrackers;

        #endregion

        #region Constructors

        /// <summary>Initialises static members of the <see cref="ResourceMgmtContext"/> class.</summary>
        static ResourceMgmtContext()
        {
            ConnectionString = ConfigurationManager.ConnectionStrings["ResourceMgmtContext"].ConnectionString;
        }
        
        /// <summary>Initialises a new instance of the <see cref="ResourceMgmtContext"/> class.</summary>
        /// <param name="identityContextProvider">The identity context provider.</param>
        public ResourceMgmtContext(IIdentityContextProvider identityContextProvider)
            : this(ConnectionString, identityContextProvider)
        {
        }
        
        /// <summary>Initialises a new instance of the <see cref="ResourceMgmtContext"/> class.</summary>
        /// <param name="connectionString">The connection string.</param>
        /// <param name="identityContextProvider">The identity context provider.</param>
        public ResourceMgmtContext(string connectionString, IIdentityContextProvider identityContextProvider)
            : base(connectionString)
        {
            ((IObjectContextAdapter)this).ObjectContext.SavingChanges += this.OnSavingChanges;

            Configuration.ProxyCreationEnabled = false;
            Configuration.LazyLoadingEnabled = false;
            this.identityContextProvider = identityContextProvider;
            this.subscribedTrackers = new List<TrackedData>();
        }

        #endregion

        #region Public methods

        /// <summary>The subscribe to change tracker.</summary>
        /// <param name="state">The state.</param>
        /// <param name="callback">The call back.</param>
        /// <typeparam name="TModel">Type to be passed to tracked data</typeparam>
        public void SubscribeToChangeTracker<TModel>(TrackedEntityState state, Action<TrackedEntity> callback)
        {
            this.subscribedTrackers.Add(new TrackedData(typeof(TModel).Name, state, callback));
        }

        /// <summary>Sounds like DB function.</summary>
        /// <param name="input">The input.</param>
        /// <returns>The <see cref="string"/>.</returns>
        [DbFunction("SqlServer", "SOUNDEX")]
        public string SoundsLike(string input)
        {
            return input;
        }

        #endregion

        #region Protected methods

        /// <summary>Event that fires on saving changes.</summary>
        /// <param name="sender">The sender.</param>
        /// <param name="eventArgs">The event args.</param>
        protected void OnSavingChanges(object sender, EventArgs eventArgs)
        {
            //// this.SaveAllDatesAsUtc(sender, eventArgs);

            this.SetCreateUpdateDetails();

            this.CallSubscribedTrackers();
        }
        
        /// <summary>The on model creating override.</summary>
        /// <param name="modelBuilder">The model builder.</param>
        protected override void OnModelCreating(DbModelBuilder modelBuilder)
        {
            modelBuilder.Conventions.AddBefore<ForeignKeyIndexConvention>(new ForeignKeyNoUnderscoreNamingConvention()); 
            modelBuilder.Conventions.Remove<PluralizingTableNameConvention>();
            
            foreach (var instance in EntityTypeConfigurations)
            {
                modelBuilder.Configurations.Add((dynamic)instance);
            }
        }

        #endregion

        #region Private methods

        /// <summary>Sets the created or modified details</summary>
        private void SetCreateUpdateDetails()
        {
            var windowsAccountId = this.identityContextProvider.GetIdentity().WindowsAccountId;

            var changedEntities = ChangeTracker.Entries().Where(entry => entry.State == EntityState.Added || entry.State == EntityState.Modified || entry.State == EntityState.Deleted);

            foreach (var changedEntity in changedEntities)
            {
                var deleteModelBase = changedEntity.Entity as DeleteModelBase;

                if (deleteModelBase != null)
                {
                    if (changedEntity.State == EntityState.Deleted)
                    {
                        changedEntity.State = EntityState.Modified;
                        
                        this.SetDeletedDetails(deleteModelBase, windowsAccountId);
                    }
                    else
                    {
                        this.SetAddedOrModifiedDetails(changedEntity, deleteModelBase, windowsAccountId);
                    }

                    continue;
                }

                var createModifyModelBase = changedEntity.Entity as CreateModifyModelBase;

                if (createModifyModelBase != null)
                {
                    this.SetAddedOrModifiedDetails(changedEntity, createModifyModelBase, windowsAccountId);
                }
            }
        }

        /// <summary>The set added or modified details.</summary>
        /// <param name="entity">The entity.</param>
        /// <param name="windowsAccountId">The windows account id.</param>
        private void SetDeletedDetails(DeleteModelBase entity, string windowsAccountId)
        {
            entity.SetDeletedDetails(windowsAccountId);
                    
            this.Entry(entity).Property(p => p.ModifiedBy).IsModified = true;
            this.Entry(entity).Property(p => p.ModifiedDate).IsModified = true;
            this.Entry(entity).Property(p => p.RecordStatus).IsModified = true;
        }

        /// <summary>The set added or modified details.</summary>
        /// <param name="changedEntity">The changed entity.</param>
        /// <param name="entity">The entity.</param>
        /// <param name="windowsAccountId">The windows account id.</param>
        private void SetAddedOrModifiedDetails(DbEntityEntry changedEntity, CreateModifyModelBase entity, string windowsAccountId)
        {
            switch (changedEntity.State)
            {
                case EntityState.Added:
                    entity.SetCreatedDetails(windowsAccountId);
                    break;

                case EntityState.Modified:
                    entity.SetModifiedDetails(windowsAccountId);
                    this.Entry(entity).Property(p => p.ModifiedBy).IsModified = true;
                    this.Entry(entity).Property(p => p.ModifiedDate).IsModified = true;
                    break;
            }
        }

        /// <summary>Call subscribed trackers.</summary>
        private void CallSubscribedTrackers()
        {
            if (this.subscribedTrackers.Any())
            {
                var changeTrack = this.ChangeTracker.Entries().Where(p => p.State == EntityState.Added || p.State == EntityState.Deleted || p.State == EntityState.Modified);
                foreach (var entry in changeTrack)
                {
                    if (entry.Entity != null)
                    {
                        string entityName = ObjectContext.GetObjectType(entry.Entity.GetType()).Name;

                        var potentialSubscribedTrackers = this.subscribedTrackers.Where(x => x.EntityName == entityName && (x.State == TrackedEntityState.All || (int)x.State == (int)entry.State)).ToList();
                        if (potentialSubscribedTrackers.Any())
                        {
                            var trackedProperties = new List<TrackedProperty>();
                            var trackedState = TrackedEntityState.Modified;
                            
                            switch (entry.State)
                            {
                                case EntityState.Modified:
                                    trackedState = TrackedEntityState.Modified;
                                    var modifiedProperties = entry.CurrentValues.PropertyNames.Where(propertyName => entry.Property(propertyName).IsModified).ToList();
                                    
                                    foreach (string propName in modifiedProperties)
                                    {
                                        trackedProperties.Add(new TrackedProperty(propName, entry.OriginalValues[propName], entry.CurrentValues[propName]));
                                    }

                                    break;
                                case EntityState.Added:
                                    trackedState = TrackedEntityState.Added;
                                    
                                    foreach (string propName in entry.CurrentValues.PropertyNames)
                                    {
                                        trackedProperties.Add(new TrackedProperty(propName, null, entry.CurrentValues[propName]));
                                    }

                                    break;
                                case EntityState.Deleted:
                                    trackedState = TrackedEntityState.Deleted;
                                    
                                    foreach (string propName in entry.OriginalValues.PropertyNames)
                                    {
                                        trackedProperties.Add(new TrackedProperty(propName, entry.OriginalValues[propName], null));
                                    }
                                    
                                    break;
                            }

                            if (trackedProperties.Any())
                            {
                                // search keys
                                var keys = new List<KeyValuePair<string, object>>();
                                var oc = ((IObjectContextAdapter)this).ObjectContext;
                                EntityKey ek = oc.ObjectStateManager.GetObjectStateEntry(entry.Entity).EntityKey;

                                // the new item is temporary
                                if (!ek.IsTemporary)
                                {
                                    foreach (var entityKeyMember in ek.EntityKeyValues)
                                    {
                                        keys.Add(new KeyValuePair<string, object>(entityKeyMember.Key, entityKeyMember.Value));
                                    } 
                                }

                                // call back
                                potentialSubscribedTrackers.ForEach(x => x.Callback.Invoke(new TrackedEntity(entityName, keys, trackedState, trackedProperties)));
                            }
                        }
                    }
                }
            }
        }

        #endregion
    }
}