﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using Newtonsoft.Json.Linq;
using Salesforce.SDK.Auth;
using Salesforce.SDK.SmartStore.Store;
using Salesforce.SDK.SmartSync.Model;
using Salesforce.SDK.SmartSync.Util;

namespace Salesforce.SDK.SmartSync.Manager
{
    public class CacheManager
    {
        public enum CachePolicy
        {
            IgnoreCacheData, // Ignores cache data and always loads from server.
            ReloadAndReturnCacheOnFailure, // Always reloads data and returns cache data only on failure.
            ReturnCacheDataDontReload, // Returns cache data and does not refresh cache if cache exists.
            ReloadAndReturnCacheData, // Reloads and returns cache data.
            ReloadIfExpiredAndReturnCacheData,
            // Refreshes cache if the refresh time interval is up and returns cache data.
            InvalidateCacheDontReload, // Invalidates the cache and does not refresh the cache.
            InvalidateCacheAndReload // Invalidates the cache and refreshes the cache.
        }

        public const string CacheKey = "cache_key";
        public const string CacheData = "cache_data";
        public const string SoupOfSoups = "master_soup";
        public const string SoupNamesKey = "soup_names";
        public const string RawData = "rawData";
        public const string SfdcType = "type";

        private static volatile Dictionary<string, CacheManager> _instances;
        private static readonly object cachelock = new Object();

        private readonly SmartStore.Store.SmartStore _smartStore;
        private Dictionary<string, List<SalesforceObject>> _objectCacheMap;
        private Dictionary<string, List<SalesforceObjectType>> _objectTypeCacheMap;
        private Dictionary<string, List<SalesforceObjectTypeLayout>> _objectTypeLayoutCacheMap;

        private CacheManager(Account account, string communityId)
        {
            _smartStore = new SmartStore.Store.SmartStore();
            ResetInMemoryCache();
        }

        public static CacheManager GetInstance(Account account)
        {
            return GetInstance(account, null);
        }

        private static string GenerateUniqueId(Account account, string communityId)
        {
            if (account == null)
            {
                throw new SmartStoreException("Account cannot be null");
            }
            string uniqueId;
            if (Account.InternalCommunityId.Equals(communityId))
            {
                communityId = null;
            }
            if (!String.IsNullOrWhiteSpace(communityId))
            {
                uniqueId = account.UserId + communityId;
            }
            else
            {
                uniqueId = account.UserId;
            }
            return uniqueId;
        }

        public static CacheManager GetInstance(Account account, string communityId)
        {
            if (account == null)
            {
                account = AccountManager.GetAccount();
            }
            if (account == null)
            {
                return null;
            }
            string uniqueId = GenerateUniqueId(account, communityId);
            lock (cachelock)
            {
                CacheManager instance = null;
                if (_instances != null)
                {
                    if (_instances.TryGetValue(uniqueId, out instance)) return instance;
                    instance = new CacheManager(account, communityId);
                    _instances.Add(uniqueId, instance);
                }
                else
                {
                    _instances = new Dictionary<string, CacheManager>();
                    instance = new CacheManager(account, communityId);
                    _instances.Add(uniqueId, instance);
                }
                instance.ResetInMemoryCache();
                return instance;
            }
        }

        public static void SoftReset(Account account)
        {
            SoftReset(account, null);
        }

        public static void SoftReset(Account account, string communityId)
        {
            if (account == null)
            {
                account = AccountManager.GetAccount();
            }
            if (account != null)
            {
                lock (cachelock)
                {
                    CacheManager instance = GetInstance(account, communityId);
                    if (instance == null) return;
                    instance.ResetInMemoryCache();
                    _instances.Remove(GenerateUniqueId(account, communityId));
                }
            }
        }

        public static void HardReset(Account account)
        {
            HardReset(account, null);
        }

        public static void HardReset(Account account, string communityId)
        {
            if (account == null)
            {
                account = AccountManager.GetAccount();
            }
            if (account != null)
            {
                lock (cachelock)
                {
                    CacheManager instance = GetInstance(account, communityId);
                    if (instance == null) return;
                    instance.CleanCache();
                    _instances.Remove(GenerateUniqueId(account, communityId));
                }
            }
        }

        public bool DoesCacheExist(string soupName)
        {
            return !String.IsNullOrWhiteSpace(soupName) && _smartStore.HasSoup(soupName);
        }

        public void RemoveCache(string soupName)
        {
            if (!String.IsNullOrWhiteSpace(soupName))
            {
                return;
            }
            if (DoesCacheExist(soupName))
            {
                _smartStore.DropSoup(soupName);
                RemoveSoupNameFromMasterSoup(soupName);
                ResetInMemoryCache();
            }
        }

        public bool NeedToReloadCache(bool cacheExists, CachePolicy cachePolicy, long lastCachedTime,
            long refreshifOlderThan)
        {
            if (CachePolicy.IgnoreCacheData == cachePolicy ||
                CachePolicy.ReturnCacheDataDontReload == cachePolicy ||
                CachePolicy.InvalidateCacheDontReload == cachePolicy)
            {
                return false;
            }
            if (CachePolicy.ReloadAndReturnCacheData == cachePolicy ||
                CachePolicy.ReloadAndReturnCacheOnFailure == cachePolicy ||
                CachePolicy.InvalidateCacheAndReload == cachePolicy)
            {
                return true;
            }
            if (!cacheExists || refreshifOlderThan <= 0 || lastCachedTime <= 0)
            {
                return true;
            }
            long timeDiff = SmartStore.Store.SmartStore.CurrentTimeMillis - lastCachedTime;
            return (timeDiff > refreshifOlderThan);
        }

        public long GetLastCacheUpdateTime(string cacheType, string cacheKey)
        {
            if (String.IsNullOrWhiteSpace(cacheType) || String.IsNullOrWhiteSpace(cacheKey) ||
                !DoesCacheExist(cacheType))
            {
                return 0;
            }
            try
            {
                QuerySpec querySpec = QuerySpec.BuildExactQuerySpec(cacheType, CacheKey, cacheKey, 1);
                JArray results = _smartStore.Query(querySpec, 0);
                if (results != null && results.Count > 0)
                {
                    var jObj = results[0].Value<JObject>();
                    if (jObj != null)
                    {
                        return jObj.ExtractValue<long>(SmartStore.Store.SmartStore.SoupLastModifiedDate);
                    }
                }
            }
            catch (SmartStoreException)
            {
                Debug.WriteLine("SmartStoreException occurred while attempting to read last cached time");
            }
            return 0;
        }

        public List<SalesforceObjectType> ReadObjectTypes(string cacheType, string cacheKey)
        {
            var cachedList = new List<SalesforceObjectType>();
            if (String.IsNullOrWhiteSpace(cacheType) || String.IsNullOrWhiteSpace(cacheKey) ||
                !DoesCacheExist(cacheType))
            {
                return cachedList;
            }

            if (_objectTypeCacheMap != null && _objectTypeCacheMap.ContainsKey(cacheKey))
            {
                return _objectTypeCacheMap[cacheKey];
            }
            try
            {
                QuerySpec querySpec = QuerySpec.BuildExactQuerySpec(cacheType,
                    CacheKey, cacheKey, 1);
                JArray results = _smartStore.Query(querySpec, 0);
                if (results != null && results.Count > 0)
                {
                    var jObj = results[0].Value<JObject>();
                    if (jObj != null)
                    {
                        var res = jObj.ExtractValue<JArray>(CacheData);
                        if (res != null && res.Count > 0)
                        {
                            for (int j = 0, max = res.Count; j < max; j++)
                            {
                                var sfObj = res[j].Value<JObject>();
                                if (sfObj != null)
                                {
                                    cachedList.Add(new SalesforceObjectType(sfObj));
                                }
                            }
                            if (cachedList.Count > 0)
                            {
                                // Inserts or updates data in memory cache.
                                if (_objectTypeCacheMap != null)
                                {
                                    if (_objectTypeCacheMap.ContainsKey(cacheKey))
                                    {
                                        _objectTypeCacheMap.Remove(cacheKey);
                                    }
                                    _objectTypeCacheMap.Add(cacheKey, cachedList);
                                }
                            }
                        }
                    }
                }
            }
            catch (SmartStoreException)
            {
                Debug.WriteLine("SmartStoreException occurred while attempting to read cached data");
            }
            return cachedList;
        }

        public List<SalesforceObject> ReadObjects(string cacheType, string cacheKey)
        {
            var cachedList = new List<SalesforceObject>();
            if (String.IsNullOrWhiteSpace(cacheType) || String.IsNullOrWhiteSpace(cacheKey) ||
                !DoesCacheExist(cacheType))
            {
                return cachedList;
            }

            if (_objectCacheMap != null && _objectCacheMap.ContainsKey(cacheKey))
            {
                return _objectCacheMap[cacheKey];
            }
            try
            {
                QuerySpec querySpec = QuerySpec.BuildExactQuerySpec(cacheType,
                    CacheKey, cacheKey, 1);
                JArray results = _smartStore.Query(querySpec, 0);
                if (results != null && results.Count > 0)
                {
                    var jObj = results[0].Value<JObject>();
                    if (jObj != null)
                    {
                        var res = jObj.ExtractValue<JArray>(CacheData);
                        if (res != null && res.Count > 0)
                        {
                            for (int j = 0, max = res.Count; j < max; j++)
                            {
                                var sfObj = res[j].Value<JObject>();
                                if (sfObj != null)
                                {
                                    cachedList.Add(new SalesforceObject(sfObj));
                                }
                            }
                            if (cachedList.Count > 0)
                            {
                                // Inserts or updates data in memory cache.
                                if (_objectCacheMap != null)
                                {
                                    if (_objectTypeCacheMap.ContainsKey(cacheKey))
                                    {
                                        _objectTypeCacheMap.Remove(cacheKey);
                                    }
                                    _objectCacheMap.Add(cacheKey, cachedList);
                                }
                            }
                        }
                    }
                }
            }
            catch (SmartStoreException)
            {
                Debug.WriteLine("SmartStoreException occurred while attempting to read cached data");
            }
            return cachedList;
        }

        public List<SalesforceObjectTypeLayout> ReadObjectLayouts(string cacheType, string cacheKey)
        {
            var cachedList = new List<SalesforceObjectTypeLayout>();
            if (String.IsNullOrWhiteSpace(cacheType) || String.IsNullOrWhiteSpace(cacheKey) ||
                !DoesCacheExist(cacheType))
            {
                return cachedList;
            }

            if (_objectTypeLayoutCacheMap != null && _objectTypeLayoutCacheMap.ContainsKey(cacheKey))
            {
                return _objectTypeLayoutCacheMap[cacheKey];
            }
            try
            {
                QuerySpec querySpec = QuerySpec.BuildExactQuerySpec(cacheType,
                    CacheKey, cacheKey, 1);
                JArray results = _smartStore.Query(querySpec, 0);
                if (results != null && results.Count > 0)
                {
                    var jObj = results[0].Value<JObject>();
                    if (jObj != null)
                    {
                        var res = jObj.ExtractValue<JArray>(CacheData);
                        if (res != null && res.Count > 0)
                        {
                            for (int j = 0, max = res.Count; j < max; j++)
                            {
                                var sfObj = res[j].Value<JObject>();
                                if (sfObj != null)
                                {
                                    var rawData = sfObj.ExtractValue<JObject>("rawData");
                                    var type = sfObj.ExtractValue<string>("type");
                                    if (rawData != null && !String.IsNullOrWhiteSpace(type))
                                    {
                                        cachedList.Add(new SalesforceObjectTypeLayout(type, rawData));
                                    }
                                }
                            }
                            if (cachedList.Count > 0)
                            {
                                // Inserts or updates data in memory cache.
                                if (_objectTypeLayoutCacheMap != null)
                                {
                                    if (_objectTypeLayoutCacheMap.ContainsKey(cacheKey))
                                    {
                                        _objectTypeLayoutCacheMap.Remove(cacheKey);
                                    }
                                    _objectTypeLayoutCacheMap.Add(cacheKey, cachedList);
                                }
                            }
                        }
                    }
                }
            }
            catch (SmartStoreException)
            {
                Debug.WriteLine("SmartStoreException occurred while attempting to read cached data");
            }
            return cachedList;
        }

        public void WriteObjectTypes(List<SalesforceObjectType> objectTypes, string cacheKey, string cacheType)
        {
            if (!WriteCache(objectTypes, cacheKey, cacheType, _objectTypeCacheMap)) return;
            // Inserts or updates data in smart store.
            var data = new JArray();
            foreach (SalesforceObjectType objectType in objectTypes)
            {
                if (objectType != null)
                {
                    data.Add(objectType.RawData);
                }
            }
            if (data.Count <= 0) return;
            var jObj = new JObject {{CacheKey, cacheKey}, {CacheData, data}};
            try
            {
                UpsertData(cacheType, jObj, cacheKey);
            }
            catch (SmartStoreException)
            {
                Debug.WriteLine("SmartStoreException occurred while attempting to cache data");
            }
        }

        public void WriteObjectLayouts(List<SalesforceObjectTypeLayout> objectLayouts, string cacheKey, string cacheType)
        {
            if (!WriteCache(objectLayouts, cacheKey, cacheType, _objectTypeLayoutCacheMap)) return;
            // Inserts or updates data in smart store.
            var data = new JArray();
            foreach (SalesforceObjectTypeLayout objectLayout in objectLayouts)
            {
                if (objectLayout != null)
                {
                    var item = new JObject {{RawData, objectLayout.RawData}, {SfdcType, objectLayout.ObjectType}};
                    data.Add(item);
                }
            }
            if (data.Count <= 0) return;
            var jObj = new JObject {{CacheKey, cacheKey}, {CacheData, data}};
            try
            {
                UpsertData(cacheType, jObj, cacheKey);
            }
            catch (SmartStoreException)
            {
                Debug.WriteLine("SmartStoreException occurred while attempting to cache data");
            }
        }

        public void WriteObjects(List<SalesforceObject> objects, string cacheKey, string cacheType)
        {
            if (!WriteCache(objects, cacheKey, cacheType, _objectCacheMap)) return;
            // Inserts or updates data in smart store.
            var data = new JArray();
            foreach (SalesforceObject obj in objects)
            {
                if (obj != null)
                {
                    data.Add(obj.RawData);
                }
            }
            if (data.Count <= 0) return;
            var jObj = new JObject {{CacheKey, cacheKey}, {CacheData, data}};
            try
            {
                UpsertData(cacheType, jObj, cacheKey);
            }
            catch (SmartStoreException)
            {
                Debug.WriteLine("SmartStoreException occurred while attempting to cache data");
            }
        }

        /// <summary>
        /// Helper method; validation and cache updates for the write methods.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="objects"></param>
        /// <param name="cacheKey"></param>
        /// <param name="cacheType"></param>
        /// <param name="cache"></param>
        /// <returns></returns>
        private bool WriteCache<T>(List<T> objects, string cacheKey, string cacheType, IDictionary<string, List<T>> cache)
        {
            if (objects == null || String.IsNullOrWhiteSpace(cacheKey) || String.IsNullOrWhiteSpace(cacheType) ||
                objects.Count == 0)
            {
                return false;
            }
            // Inserts or updates data in memory cache.
            if (cache != null)
            {
                if (cache.ContainsKey(cacheKey))
                {
                    cache.Remove(cacheKey);
                }
                cache.Add(cacheKey, objects);
            }
            return true;
        }

        private void RegisterSoup(string soupName, string cacheKey)
        {
            RegisterMasterSoup();
            if (!DoesCacheExist(soupName))
            {
                IndexSpec[] indexSpecs = {new IndexSpec(CacheKey, SmartStoreType.SmartString)};
                _smartStore.RegisterSoup(soupName, indexSpecs);
            }
        }

        private void RegisterMasterSoup()
        {
            if (!DoesCacheExist(SoupOfSoups))
            {
                IndexSpec[] indexSpecs = {new IndexSpec(SoupNamesKey, SmartStoreType.SmartString)};
                _smartStore.RegisterSoup(SoupOfSoups, indexSpecs);
            }
        }

        private void UpsertData(string soupName, JObject jObject, string cacheKey)
        {
            if (jObject == null || String.IsNullOrWhiteSpace(soupName))
            {
                return;
            }
            RegisterSoup(soupName, cacheKey);
            try
            {
                _smartStore.Upsert(soupName, jObject, cacheKey);
                AddSoupNameToMasterSoup(soupName);
            }
            catch (SmartStoreException)
            {
                Debug.WriteLine("SmartStoreException occurred while attempting to cache data");
            }
        }

        private JArray GetAllSoupNames()
        {
            const string smartSql = "SELECT {" + SoupOfSoups + ":" + SoupNamesKey + "} FROM {" + SoupOfSoups + "}";
            JArray results = null;
            QuerySpec querySpec = QuerySpec.BuildSmartQuerySpec(smartSql, 1);
            try
            {
                long count = _smartStore.CountQuery(querySpec);
                querySpec = QuerySpec.BuildSmartQuerySpec(smartSql, (int) count);
                results = _smartStore.Query(querySpec, 0);
            }
            catch (SmartStoreException e)
            {
                Debug.WriteLine("SmartStoreException occurred while attempting to read cached data", e);
            }
            return results ?? new JArray();
        }

        private bool DoesMasterSoupContainSoup(String soupName)
        {
            JArray soupNames = GetAllSoupNames();
            for (int i = 0, max = soupNames.Count; i < max; i++)
            {
                var names = soupNames[i].Value<JArray>();
                if (names != null && names.Count > 0)
                {
                    string name = names[0].ToString();
                    if (soupName.Equals(name))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private void AddSoupNameToMasterSoup(string soupName)
        {
            if (DoesMasterSoupContainSoup(soupName))
            {
                return;
            }
            try
            {
                var soup = new JObject {{SoupNamesKey, soupName}};
                _smartStore.Upsert(SoupOfSoups, soup);
            }
            catch (SmartStoreException)
            {
                Debug.WriteLine("SmartStoreException occurred while attempting to cache data");
            }
        }

        private void RemoveSoupNameFromMasterSoup(String soupName)
        {
            if (!DoesMasterSoupContainSoup(soupName))
            {
                return;
            }
            try
            {
                long soupEntryId = _smartStore.LookupSoupEntryId(SoupOfSoups,
                    SoupNamesKey, soupName);
                _smartStore.Delete(SoupOfSoups, new[] {soupEntryId}, false);
            }
            catch (SmartStoreException e)
            {
                Debug.WriteLine("SmartStoreException occurred while attempting to cache data: ", e.ToString());
            }
        }

        private void ClearMasterSoup()
        {
            _smartStore.DropSoup(SoupOfSoups);
        }

        private void ClearAllSoups()
        {
            JArray soupNames = GetAllSoupNames();
            for (int i = 0, max = soupNames.Count; i < max; i++)
            {
                var names = soupNames[i].Value<JArray>();
                if (names != null && names.Count > 0)
                {
                    string name = names[0].ToString();
                    _smartStore.DropSoup(name);
                }
            }
        }

        private void CleanCache()
        {
            ResetInMemoryCache();
            Account account = AccountManager.GetAccount();
            if (account != null)
            {
                CacheManager instance = GetInstance(account, account.CommunityId);
                if (instance == null || instance._smartStore == null) return;
                instance._smartStore.DropAllSoups();
            }
        }

        private void ResetInMemoryCache()
        {
            _objectCacheMap = new Dictionary<string, List<SalesforceObject>>();
            _objectTypeCacheMap = new Dictionary<string, List<SalesforceObjectType>>();
            _objectTypeLayoutCacheMap = new Dictionary<string, List<SalesforceObjectTypeLayout>>();
        }
    }
}