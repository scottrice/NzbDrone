﻿using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Composition;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.ThingiProvider.Events;

namespace NzbDrone.Core.ThingiProvider
{
    public abstract class ProviderFactory<TProvider, TProviderDefinition> : IProviderFactory<TProvider, TProviderDefinition>, IHandle<ApplicationStartedEvent>
        where TProviderDefinition : ProviderDefinition, new()
        where TProvider : IProvider
    {
        private readonly IProviderRepository<TProviderDefinition> _providerRepository;
        private readonly IContainer _container;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;

        protected readonly List<TProvider> _providers;

        protected ProviderFactory(IProviderRepository<TProviderDefinition> providerRepository,
                                  IEnumerable<TProvider> providers,
                                  IContainer container,
                                  IEventAggregator eventAggregator,
                                  Logger logger)
        {
            _providerRepository = providerRepository;
            _container = container;
            _eventAggregator = eventAggregator;
            _providers = providers.ToList();
            _logger = logger;
        }

        public List<TProviderDefinition> All()
        {
            return _providerRepository.All().ToList();
        }

        public List<TProviderDefinition> Templates()
        {
            return _providers.Select(GetTemplate).ToList();
        }

        public List<TProvider> GetAvailableProviders()
        {
            return Active().Select(GetInstance).ToList();
        }

        public TProviderDefinition Get(int id)
        {
            return _providerRepository.Get(id);
        }

        public virtual TProviderDefinition Create(TProviderDefinition definition)
        {
            return _providerRepository.Insert(definition);
        }

        public virtual void Update(TProviderDefinition definition)
        {
            _providerRepository.Update(definition);
            _eventAggregator.PublishEvent(new ProviderUpdatedEvent<TProvider>());
        }

        public void Delete(int id)
        {
            _providerRepository.Delete(id);
        }

        protected TProvider GetInstance(TProviderDefinition definition)
        {
            var type = GetImplementation(definition);
            var instance = (TProvider)_container.Resolve(type);
            instance.Definition = definition;
            return instance;
        }

        private Type GetImplementation(TProviderDefinition definition)
        {
            return _providers.Select(c => c.GetType()).SingleOrDefault(c => c.Name.Equals(definition.Implementation, StringComparison.InvariantCultureIgnoreCase));
        }

        protected virtual TProviderDefinition GetTemplate(TProvider provider)
        {
            var definition = new TProviderDefinition()
            {
                ConfigContract = provider.ConfigContract.Name,
                Implementation = provider.GetType().Name,
                Settings = (IProviderConfig)Activator.CreateInstance(provider.ConfigContract)
            };

            return definition;
        }

        public void Handle(ApplicationStartedEvent message)
        {
            _logger.Debug("Initializing Providers. Count {0}", _providers.Count);

            RemoveMissingImplementations();

            InitializeProviders();
        }

        protected virtual void InitializeProviders()
        {
        }

        protected virtual List<TProviderDefinition> Active()
        {
            return All().Where(c => c.Settings.Validate().IsValid).ToList();
        }

        private void RemoveMissingImplementations()
        {
            var storedProvider = _providerRepository.All();

            foreach (var invalidDefinition in storedProvider.Where(def => GetImplementation(def) == null))
            {
                _logger.Debug("Removing {0} ", invalidDefinition.Name);
                _providerRepository.Delete(invalidDefinition);
            }
        }
    }
}