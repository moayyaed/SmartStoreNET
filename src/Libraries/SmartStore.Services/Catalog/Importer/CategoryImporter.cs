﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SmartStore.Core.Async;
using SmartStore.Core.Data;
using SmartStore.Core.Domain.Catalog;
using SmartStore.Core.Domain.DataExchange;
using SmartStore.Core.Domain.Media;
using SmartStore.Core.Domain.Seo;
using SmartStore.Core.Events;
using SmartStore.Services.DataExchange.Import;
using SmartStore.Services.Localization;
using SmartStore.Services.Media;
using SmartStore.Services.Seo;
using SmartStore.Services.Stores;
using SmartStore.Utilities;
using SmartStore.Core.Domain.Stores;

namespace SmartStore.Services.Catalog.Importer
{
	public class CategoryImporter : EntityImporterBase
	{
		private readonly IRepository<Category> _categoryRepository;
		private readonly IRepository<UrlRecord> _urlRecordRepository;
		private readonly IRepository<Picture> _pictureRepository;
		private readonly ICommonServices _services;
		private readonly IUrlRecordService _urlRecordService;
		private readonly IRepository<StoreMapping> _storeMappingRepository;
		private readonly ICategoryTemplateService _categoryTemplateService;
		private readonly IStoreMappingService _storeMappingService;
		private readonly IPictureService _pictureService;
		private readonly ILocalizedEntityService _localizedEntityService;
		private readonly FileDownloadManager _fileDownloadManager;
		private readonly SeoSettings _seoSettings;
		private readonly DataExchangeSettings _dataExchangeSettings;

		public CategoryImporter(
			IRepository<Category> categoryRepository,
			IRepository<UrlRecord> urlRecordRepository,
			IRepository<Picture> pictureRepository,
			IRepository<StoreMapping> storeMappingRepository,
			ICommonServices services,
			IUrlRecordService urlRecordService,
			ICategoryTemplateService categoryTemplateService,
			IStoreMappingService storeMappingService,
			IPictureService pictureService,
			ILocalizedEntityService localizedEntityService,
			FileDownloadManager fileDownloadManager,
			SeoSettings seoSettings,
			DataExchangeSettings dataExchangeSettings)
		{
			_categoryRepository = categoryRepository;
			_urlRecordRepository = urlRecordRepository;
			_pictureRepository = pictureRepository;
			_storeMappingRepository = storeMappingRepository;
			_services = services;
			_urlRecordService = urlRecordService;
			_categoryTemplateService = categoryTemplateService;
			_storeMappingService = storeMappingService;
			_pictureService = pictureService;
			_localizedEntityService = localizedEntityService;
			_fileDownloadManager = fileDownloadManager;
			_seoSettings = seoSettings;
			_dataExchangeSettings = dataExchangeSettings;
		}

		protected virtual int ProcessSlugs(IImportExecuteContext context, IEnumerable<ImportRow<Category>> batch)
		{
			var entityName = typeof(Category).Name;
			var slugMap = new Dictionary<string, UrlRecord>();

			Func<string, UrlRecord> slugLookup = ((s) =>
			{
				return (slugMap.ContainsKey(s) ? slugMap[s] : null);
			});

			foreach (var row in batch)
			{
				if (!(row.Segmenter.HasColumn("SeName") || row.IsNew || row.NameChanged))
					continue;

				try
				{
					UrlRecord urlRecord = null;
					var seName = row.GetDataValue<string>("SeName");
					seName = row.Entity.ValidateSeName(seName, row.Entity.Name, true, _urlRecordService, _seoSettings, extraSlugLookup: slugLookup);

					if (row.IsNew)
					{
						// dont't bother validating SeName for new entities.
						urlRecord = new UrlRecord
						{
							EntityId = row.Entity.Id,
							EntityName = entityName,
							Slug = seName,
							LanguageId = 0,
							IsActive = true,
						};
						_urlRecordRepository.Insert(urlRecord);
					}
					else
					{
						urlRecord = _urlRecordService.SaveSlug(row.Entity, seName, 0);
					}

					if (urlRecord != null)
					{
						// a new record was inserted to the store: keep track of it for this batch.
						slugMap[seName] = urlRecord;
					}

					foreach (var lang in context.Languages)
					{
						seName = row.GetDataValue<string>("SeName", lang.UniqueSeoCode);
						if (seName.HasValue())
						{
							seName = row.Entity.ValidateSeName(seName, null, false, _urlRecordService, _seoSettings, lang.Id, slugLookup);

							urlRecord = _urlRecordService.SaveSlug(row.Entity, seName, lang.Id);

							if (urlRecord != null)
							{
								slugMap[seName] = urlRecord;
							}
						}
					}
				}
				catch (Exception exception)
				{
					context.Result.AddWarning(exception.Message, row.GetRowInfo(), "SeName");
				}
			}

			// commit whole batch at once
			return _urlRecordRepository.Context.SaveChanges();
		}

		protected virtual int ProcessLocalizations(IImportExecuteContext context, IEnumerable<ImportRow<Category>> batch)
		{
			foreach (var row in batch)
			{
				foreach (var lang in context.Languages)
				{
					var name = row.GetDataValue<string>("Name", lang.UniqueSeoCode);
					var fullName = row.GetDataValue<string>("FullName", lang.UniqueSeoCode);
					var description = row.GetDataValue<string>("Description", lang.UniqueSeoCode);
					var bottomDescription = row.GetDataValue<string>("BottomDescription", lang.UniqueSeoCode);
					var metaKeywords = row.GetDataValue<string>("MetaKeywords", lang.UniqueSeoCode);
					var metaDescription = row.GetDataValue<string>("MetaDescription", lang.UniqueSeoCode);
					var metaTitle = row.GetDataValue<string>("MetaTitle", lang.UniqueSeoCode);

					_localizedEntityService.SaveLocalizedValue(row.Entity, x => x.Name, name, lang.Id);
					_localizedEntityService.SaveLocalizedValue(row.Entity, x => x.FullName, fullName, lang.Id);
					_localizedEntityService.SaveLocalizedValue(row.Entity, x => x.Description, description, lang.Id);
					_localizedEntityService.SaveLocalizedValue(row.Entity, x => x.BottomDescription, bottomDescription, lang.Id);
					_localizedEntityService.SaveLocalizedValue(row.Entity, x => x.MetaKeywords, metaKeywords, lang.Id);
					_localizedEntityService.SaveLocalizedValue(row.Entity, x => x.MetaDescription, metaDescription, lang.Id);
					_localizedEntityService.SaveLocalizedValue(row.Entity, x => x.MetaTitle, metaTitle, lang.Id);
				}
			}

			var num = _categoryRepository.Context.SaveChanges();

			return num;
		}

		protected virtual int ProcessParentMappings(
			IImportExecuteContext context,
			IEnumerable<ImportRow<Category>> batch,
			Dictionary<int, ImportCategoryMapping> srcToDestId)
		{
			foreach (var row in batch)
			{
				var id = row.GetDataValue<int>("Id");
				var rawParentId = row.GetDataValue<string>("ParentCategoryId");
				var parentId = rawParentId.ToInt(-1);

				if (id != 0 && parentId != -1 && srcToDestId.ContainsKey(id) && srcToDestId.ContainsKey(parentId))
				{
					// only touch hierarchical data if child and parent were inserted
					if (srcToDestId[id].Inserted && srcToDestId[parentId].Inserted && srcToDestId[id].DestinationId != 0)
					{
						var category = _categoryRepository.GetById(srcToDestId[id].DestinationId);
						if (category != null)
						{
							category.ParentCategoryId = srcToDestId[parentId].DestinationId;

							_categoryRepository.Update(category);
						}
					}
				}
			}

			var num = _categoryRepository.Context.SaveChanges();

			return num;
		}

		protected virtual int ProcessPictures(
			IImportExecuteContext context,
			IEnumerable<ImportRow<Category>> batch,
			Dictionary<int, ImportCategoryMapping> srcToDestId)
		{
			Picture picture = null;
			var equalPictureId = 0;

			foreach (var row in batch)
			{
				try
				{
					var srcId = row.GetDataValue<int>("Id");
					var urlOrPath = row.GetDataValue<string>("ImageUrl");

					if (srcId != 0 && srcToDestId.ContainsKey(srcId) && urlOrPath.HasValue())
					{
						var currentPictures = new List<Picture>();
						var category = _categoryRepository.GetById(srcToDestId[srcId].DestinationId);
						var seoName = _pictureService.GetPictureSeName(row.EntityDisplayName);
						var image = CreateDownloadImage(urlOrPath, seoName, 1);

						if (category != null && image != null)
						{
							if (image.Url.HasValue() && !image.Success.HasValue)
							{
								AsyncRunner.RunSync(() => _fileDownloadManager.DownloadAsync(DownloaderContext, new FileDownloadManagerItem[] { image }));
							}

							if ((image.Success ?? false) && File.Exists(image.Path))
							{
								Succeeded(image);
								var pictureBinary = File.ReadAllBytes(image.Path);

								if (pictureBinary != null && pictureBinary.Length > 0)
								{
									if (category.PictureId.HasValue && (picture = _pictureRepository.GetById(category.PictureId.Value)) != null)
										currentPictures.Add(picture);

									pictureBinary = _pictureService.ValidatePicture(pictureBinary);
									pictureBinary = _pictureService.FindEqualPicture(pictureBinary, currentPictures, out equalPictureId);

									if (pictureBinary != null && pictureBinary.Length > 0)
									{
										if ((picture = _pictureService.InsertPicture(pictureBinary, image.MimeType, seoName, true, false, false)) != null)
										{
											category.PictureId = picture.Id;

											_categoryRepository.Update(category);
										}
									}
									else
									{
										context.Result.AddInfo("Found equal picture in data store. Skipping field.", row.GetRowInfo(), "ImageUrls");
									}
								}
							}
							else if (image.Url.HasValue())
							{
								context.Result.AddInfo("Download of an image failed.", row.GetRowInfo(), "ImageUrls");
							}
						}
					}
				}
				catch (Exception exception)
				{
					context.Result.AddWarning(exception.ToAllMessages(), row.GetRowInfo(), "ImageUrls");
				}
			}

			var num = _categoryRepository.Context.SaveChanges();

			return num;
		}

		protected virtual int ProcessStoreMappings(IImportExecuteContext context, IEnumerable<ImportRow<Category>> batch)
		{
			_storeMappingRepository.AutoCommitEnabled = false;

			foreach (var row in batch)
			{
				var storeIds = row.GetDataValue<List<int>>("StoreIds");
				if (!storeIds.IsNullOrEmpty())
				{
					_storeMappingService.SaveStoreMappings(row.Entity, storeIds.ToArray());
				}
			}

			// commit whole batch at once
			return _services.DbContext.SaveChanges();
		}

		protected virtual int ProcessCategories(
			IImportExecuteContext context,
			IEnumerable<ImportRow<Category>> batch,
			Dictionary<string, int> templateViewPaths,
			Dictionary<int, ImportCategoryMapping> srcToDestId)
		{
			_categoryRepository.AutoCommitEnabled = true;

			Category lastInserted = null;
			Category lastUpdated = null;
			var defaultTemplateId = templateViewPaths["CategoryTemplate.ProductsInGridOrLines"];

			foreach (var row in batch)
			{
				Category category = null;
				var id = row.GetDataValue<int>("Id");
				var name = row.GetDataValue<string>("Name");

				foreach (var keyName in context.KeyFieldNames)
				{
					switch (keyName)
					{
						case "Id":
							if (id != 0)
								category = _categoryRepository.GetById(id);
							break;
						case "Name":
							if (name.HasValue())
								category = _categoryRepository.Table.FirstOrDefault(x => x.Name == name);
							break;
					}

					if (category != null)
						break;
				}

				if (category == null)
				{
					if (context.UpdateOnly)
					{
						++context.Result.SkippedRecords;
						continue;
					}

					// a Name is required with new categories
					if (!row.Segmenter.HasColumn("Name"))
					{
						++context.Result.SkippedRecords;
						context.Result.AddError("The 'Name' field is required for new categories. Skipping row.", row.GetRowInfo(), "Name");
						continue;
					}

					category = new Category();
				}

				row.Initialize(category, name ?? category.Name);

				if (!row.IsNew && !category.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
				{
					// Perf: use this later for SeName updates.
					row.NameChanged = true;
				}

				row.SetProperty(context.Result, (x) => x.Name);
				row.SetProperty(context.Result, (x) => x.FullName);
				row.SetProperty(context.Result, (x) => x.Description);
				row.SetProperty(context.Result, (x) => x.BottomDescription);
				row.SetProperty(context.Result, (x) => x.MetaKeywords);
				row.SetProperty(context.Result, (x) => x.MetaDescription);
				row.SetProperty(context.Result, (x) => x.MetaTitle);
				row.SetProperty(context.Result, (x) => x.PageSize, 12);
				row.SetProperty(context.Result, (x) => x.AllowCustomersToSelectPageSize, true);
				row.SetProperty(context.Result, (x) => x.PageSizeOptions);
				row.SetProperty(context.Result, (x) => x.PriceRanges);
				row.SetProperty(context.Result, (x) => x.ShowOnHomePage);
				row.SetProperty(context.Result, (x) => x.HasDiscountsApplied);
				row.SetProperty(context.Result, (x) => x.Published, true);
				row.SetProperty(context.Result, (x) => x.DisplayOrder);
				row.SetProperty(context.Result, (x) => x.Alias);
				row.SetProperty(context.Result, (x) => x.DefaultViewMode);
				// With new entities, "LimitedToStores" is an implicit field, meaning
				// it has to be set to true by code if it's absent but "StoreIds" exists.
				row.SetProperty(context.Result, (x) => x.LimitedToStores, !row.GetDataValue<List<int>>("StoreIds").IsNullOrEmpty());

				var tvp = row.GetDataValue<string>("CategoryTemplateViewPath");
				category.CategoryTemplateId = (tvp.HasValue() && templateViewPaths.ContainsKey(tvp) ? templateViewPaths[tvp] : defaultTemplateId);

				row.SetProperty(context.Result, (x) => x.CreatedOnUtc, UtcNow);
				category.UpdatedOnUtc = UtcNow;

				if (id != 0 && !srcToDestId.ContainsKey(id))
				{
					srcToDestId.Add(id, new ImportCategoryMapping { Inserted = row.IsTransient });
				}

				if (row.IsTransient)
				{
					_categoryRepository.Insert(category);
					lastInserted = category;
				}
				else
				{
					_categoryRepository.Update(category);
					lastUpdated = category;
				}
			}

			// commit whole batch at once
			var num = _categoryRepository.Context.SaveChanges();

			// get new category ids
			foreach (var row in batch)
			{
				var id = row.GetDataValue<int>("Id");

				if (id != 0 && srcToDestId.ContainsKey(id))
					srcToDestId[id].DestinationId = row.Entity.Id;
			}

			// Perf: notify only about LAST insertion and update
			if (lastInserted != null)
			{
				_services.EventPublisher.EntityInserted(lastInserted);
			}

			if (lastUpdated != null)
			{
				_services.EventPublisher.EntityUpdated(lastUpdated);
			}

			return num;
		}

		public static string[] SupportedKeyFields
		{
			get
			{
				return new string[] { "Id", "Name" };
			}
		}

		public static string[] DefaultKeyFields
		{
			get
			{
				return new string[] { "Name", "Id" };
			}
		}

		protected override void Import(IImportExecuteContext context)
		{
			var srcToDestId = new Dictionary<int, ImportCategoryMapping>();

			var templateViewPaths = _categoryTemplateService.GetAllCategoryTemplates().ToDictionarySafe(x => x.ViewPath, x => x.Id);

			using (var scope = new DbContextScope(ctx: _categoryRepository.Context, autoDetectChanges: false, proxyCreation: false, validateOnSave: false))
			{
				var segmenter = context.CreateSegmenter();

				Init(context, _dataExchangeSettings);

				context.Result.TotalRecords = segmenter.TotalRows;

				while (context.Abort == DataExchangeAbortion.None && segmenter.ReadNextBatch())
				{
					var batch = segmenter.GetCurrentBatch<Category>();

					// Perf: detach all entities
					_categoryRepository.Context.DetachAll(false);

					context.SetProgress(segmenter.CurrentSegmentFirstRowIndex - 1, segmenter.TotalRows);

					try
					{
						ProcessCategories(context, batch, templateViewPaths, srcToDestId);
					}
					catch (Exception exception)
					{
						context.Result.AddError(exception, segmenter.CurrentSegment, "ProcessCategories");
					}

					// reduce batch to saved (valid) products.
					// No need to perform import operations on errored products.
					batch = batch.Where(x => x.Entity != null && !x.IsTransient).ToArray();

					// update result object
					context.Result.NewRecords += batch.Count(x => x.IsNew && !x.IsTransient);
					context.Result.ModifiedRecords += batch.Count(x => !x.IsNew && !x.IsTransient);

					// process slugs
					if (segmenter.HasColumn("SeName", true) || batch.Any(x => x.IsNew || x.NameChanged))
					{
						try
						{
							_categoryRepository.Context.AutoDetectChangesEnabled = true;
							ProcessSlugs(context, batch);
						}
						catch (Exception exception)
						{
							context.Result.AddError(exception, segmenter.CurrentSegment, "ProcessSlugs");
						}
						finally
						{
							_categoryRepository.Context.AutoDetectChangesEnabled = false;
						}
					}

					// process store mappings
					if (segmenter.HasColumn("StoreIds"))
					{
						try
						{
							ProcessStoreMappings(context, batch);
						}
						catch (Exception exception)
						{
							context.Result.AddError(exception, segmenter.CurrentSegment, "ProcessStoreMappings");
						}
					}

					// localizations
					try
					{
						ProcessLocalizations(context, batch);
					}
					catch (Exception exception)
					{
						context.Result.AddError(exception, segmenter.CurrentSegment, "ProcessLocalizedProperties");
					}

					// process pictures
					if (srcToDestId.Any() && segmenter.HasColumn("ImageUrl"))
					{
						try
						{
							_categoryRepository.Context.AutoDetectChangesEnabled = true;
							ProcessPictures(context, batch, srcToDestId);
						}
						catch (Exception exception)
						{
							context.Result.AddError(exception, segmenter.CurrentSegment, "ProcessPictures");
						}
						finally
						{
							_categoryRepository.Context.AutoDetectChangesEnabled = false;
						}
					}
				}

				// map parent id of inserted categories
				if (srcToDestId.Any() && segmenter.HasColumn("Id") && segmenter.HasColumn("ParentCategoryId"))
				{
					segmenter.Reset();

					while (context.Abort == DataExchangeAbortion.None && segmenter.ReadNextBatch())
					{
						var batch = segmenter.GetCurrentBatch<Category>();
						_categoryRepository.Context.DetachAll(false);

						try
						{
							ProcessParentMappings(context, batch, srcToDestId);
						}
						catch (Exception exception)
						{
							context.Result.AddError(exception, segmenter.CurrentSegment, "ProcessParentMappings");
						}
					}
				}
			}
		}

		public class ImportCategoryMapping
		{
			public int DestinationId { get; set; }
			public bool Inserted { get; set; }
		}
	}

}
