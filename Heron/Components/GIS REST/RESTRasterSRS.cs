using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Types;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Rhino.Commands;
using Rhino.Geometry;
using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Windows.Forms;

namespace Heron
{
    public class RESTRasterSRS : HeronRasterPreviewComponent
    {
        //Class Constructor
        public RESTRasterSRS() : base("Get REST Raster", "RESTRaster_Compute", "Get raster imagery from ArcGIS REST Services.  " +
            "Use the SetSRS component to set the spatial reference system used by this component.", "GIS REST")
        {

        }


        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddCurveParameter("Boundary", "boundary", "Boundary curve(s) for imagery", GH_ParamAccess.list);
            pManager.AddIntegerParameter("Resolution", "resolution", "Maximum resolution for images", GH_ParamAccess.item, 1024);
            pManager.AddTextParameter("Target Folder", "folderPath", "Folder to save image files", GH_ParamAccess.item, Path.GetTempPath());
            pManager.AddTextParameter("Prefix", "prefix", "Prefix for image file name", GH_ParamAccess.item, "restRaster");
            pManager.AddTextParameter("REST URL", "URL", "ArcGIS REST Service website to query. Use the component \nmenu item \"Create REST Raster Source List\" for some examples.", GH_ParamAccess.item);
            pManager.AddTextParameter("Image Type", "imageType", "Image file type to download from the service.  " +
                "Some REST raster services have a range of available types including jpg, tif, png, png32, svg.", GH_ParamAccess.item, "jpg");
            pManager.AddBooleanParameter("Run", "run", "Go ahead and download imagery from the Service", GH_ParamAccess.item, false);

        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Image", "Image", "File location of downloaded image", GH_ParamAccess.tree);
            pManager.AddCurveParameter("Image Frame", "imageFrame", "Bounding box of image for mapping to geometry", GH_ParamAccess.tree);
            pManager.AddTextParameter("REST Query", "RESTQuery", "Full text of REST query", GH_ParamAccess.tree);

        }

        private string Prefix { get; set; }
        private string FolderPath { get; set; }
        public Rectangle3d ImageRectangle { get; set; }

        public string ResolutionErrorMessage = "Try smaller resolution";


        protected override void SolveInstance(IGH_DataAccess DA)
        {
            List<Curve> boundary = new List<Curve>();
            DA.GetDataList<Curve>("Boundary", boundary);

            int Res = -1;
            DA.GetData<int>("Resolution", ref Res);

            string folderPath = string.Empty;
            DA.GetData<string>("Target Folder", ref folderPath);
            if (!folderPath.EndsWith(@"\")) { folderPath = folderPath + @"\"; }
            FolderPath = folderPath;

            string prefix = string.Empty;
            DA.GetData<string>("Prefix", ref prefix);

            Prefix = prefix;

            string URL = string.Empty;
            DA.GetData<string>("REST URL", ref URL);
            if (URL.EndsWith(@"/")) { URL = URL + "export?"; }

            string imageType = string.Empty;
            DA.GetData<string>("Image Type", ref imageType);

            bool run = false;
            DA.GetData<bool>("Run", ref run);

            ///GDAL setup
            //Heron.GdalConfiguration.ConfigureOgr();
            //Heron.GdalConfiguration.ConfigureGdal();

            ///Set transform from input spatial reference to Heron spatial reference
            ///TODO: verify the userSRS is valid
            /*
            OSGeo.OSR.SpatialReference heronSRS = new OSGeo.OSR.SpatialReference("");
            heronSRS.SetFromUserInput(HeronSRS.Instance.SRS);
            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Heron's Spatial Spatial Reference System (SRS): " + HeronSRS.Instance.SRS);
            int heronSRSInt = Int16.Parse(heronSRS.GetAuthorityCode(null));
            Message = "EPSG:" + heronSRSInt;
            */

            OSGeo.OSR.SpatialReference heronSRS = new OSGeo.OSR.SpatialReference("");
            heronSRS.SetFromUserInput(HeronSRS.Instance.SRS);
            OSGeo.OSR.SpatialReference wgsSRS = new OSGeo.OSR.SpatialReference("");
            wgsSRS.SetFromUserInput("WGS84");
            //AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Heron's Spatial Spatial Reference System (SRS): " + HeronSRS.Instance.SRS);
            int heronSRSInt = Int16.Parse(heronSRS.GetAuthorityCode(null));
            Message = "EPSG:" + heronSRSInt;

            ///Apply EAP to HeronSRS
            Transform userSRSToModelTransform = Heron.Convert.GetUserSRSToHeronSRSTransform(heronSRS);
            Transform heronToUserSRSTransform = Heron.Convert.GetHeronSRSToUserSRSTransform(heronSRS);

            ImageRectangle = new Rectangle3d();
            GH_Structure<GH_String> mapList = new GH_Structure<GH_String>();
            GH_Structure<GH_String> mapquery = new GH_Structure<GH_String>();
            GH_Structure<GH_Rectangle> imgFrame = new GH_Structure<GH_Rectangle>();

            int[] smallerResolutions = new int[] {1700, 1200};

            FileInfo file = new FileInfo(folderPath);
            file.Directory.Create();

            for (int i = 0; i < boundary.Count; i++)
            {
                GH_Path path = new GH_Path(i);

                BoundingBox imageBox = boundary[i].GetBoundingBox(false);
                imageBox.Transform(heronToUserSRSTransform);

                var request = RequestBody(URL, imageBox, Res, heronSRSInt, imageType);


                string result = string.Empty;

                ///Clean up file name for png32 png16 png8 geotiff tiff
                string imageTypeShort = CleanupImageTypeName(imageType);
                string imageFullPath = $"{FolderPath}{Prefix}_{i}.{imageTypeShort}";

                if (run)
                {
                    try
                    {
                        result = DownLoadImage(request, imageFullPath);

                        if (result == ResolutionErrorMessage)
                        {
                            request = RequestBody(URL, imageBox, smallerResolutions[0], heronSRSInt, imageType);
                            result = DownLoadImage(request, imageFullPath);
                            if (result == ResolutionErrorMessage)
                            {
                                request = RequestBody(URL, imageBox, smallerResolutions[1], heronSRSInt, imageType);
                                result = DownLoadImage(request, imageFullPath);

                                if (result == ResolutionErrorMessage)
                                {
                                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Failed to download image.");
                                    DA.SetDataTree(2, mapquery);
                                    return;
                                }
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Error, e.Message);
                        DA.SetDataTree(2, mapquery);
                        return;
                    }

                    mapList.Append(new GH_String(imageFullPath), path);

                    ImageRectangle.Transform(userSRSToModelTransform);
                    if (ImageRectangle.IsValid)
                    {
                        imgFrame.Append(new GH_Rectangle(ImageRectangle), path);
                        AddPreviewItem(imageFullPath, ImageRectangle);
                    }
                }

                mapquery.Append(new GH_String($"{request}&f=image"), path);
            }


            DA.SetDataTree(0, mapList);
            DA.SetDataTree(1, imgFrame);
            DA.SetDataTree(2, mapquery);

        }

        /// <summary>
        /// Request and save image.
        /// </summary>
        /// <param name="requestBody"></param>
        /// <param name="path"></param>
        /// <param name="transform"></param>
        /// <param name="imageFileType"></param>
        /// <param name="index"></param>
        /// <returns></returns>
        private string DownLoadImage(string requestBody, string filePath)
        {
            string restqueryJSON = $"{requestBody}&f=json";
            string restqueryImage = $"{requestBody}&f=image";

            var result = String.Empty;

            // Get extent of image from arcgis rest service as JSON
            result = Heron.Convert.HttpToJson(restqueryJSON);

            JObject jObj = JsonConvert.DeserializeObject<JObject>(result);
            if (!jObj.ContainsKey("href"))
            {
                restqueryJSON = restqueryJSON.Replace("export?", "exportImage?");
                restqueryImage = restqueryImage.Replace("export?", "exportImage?");
                result = Heron.Convert.HttpToJson(restqueryJSON);
                jObj = JsonConvert.DeserializeObject<JObject>(result);
            }

            if (jObj["extent"] != null)
            {
                Point3d extMin = new Point3d((double)jObj["extent"]["xmin"], (double)jObj["extent"]["ymin"], 0);
                Point3d extMax = new Point3d((double)jObj["extent"]["xmax"], (double)jObj["extent"]["ymax"], 0);
                ImageRectangle = new Rectangle3d(Plane.WorldXY, extMin, extMax);
            }

            string imageQueryJSON = "";
            if (jObj["href"] != null)
            {
                imageQueryJSON = jObj["href"].ToString();
            }



            string imageDownloadResponse = String.Empty;

            // Try link from JSON and fall back to the original REST query if not successful
            if (!String.IsNullOrEmpty(imageQueryJSON))
            {
                imageDownloadResponse = Heron.Convert.DownloadHttpImage(imageQueryJSON, filePath);
                if (!String.IsNullOrEmpty(imageDownloadResponse))
                {
                    imageDownloadResponse = Heron.Convert.DownloadHttpImage(restqueryImage, filePath);
                }
            }
            else
            {
                imageDownloadResponse = Heron.Convert.DownloadHttpImage(restqueryImage, filePath);
            }

            // Failed to downdload image, suggest smaller resolution
            if (!String.IsNullOrEmpty(imageDownloadResponse))
            {
                imageDownloadResponse = ResolutionErrorMessage;
            }

            return imageDownloadResponse;
        }


        /// <summary>
        /// Cleanup image file type naming
        /// </summary>
        /// <param name="imageTypeName"></param>
        /// <returns></returns>
        private string CleanupImageTypeName(string imageTypeName)
        {
            if (imageTypeName.EndsWith("32"))
            {
                imageTypeName = imageTypeName.Remove(imageTypeName.LastIndexOf("32"));
            }

            if (imageTypeName.EndsWith("16"))
            {
                imageTypeName = imageTypeName.Remove(imageTypeName.LastIndexOf("16"));
            }

            if (imageTypeName.EndsWith("8"))
            {
                imageTypeName = imageTypeName.Remove(imageTypeName.LastIndexOf("8"));
            }
            if (imageTypeName.EndsWith("geotiff", true, null))
            {
                imageTypeName = imageTypeName.Remove(imageTypeName.LastIndexOf("geotiff"));
                imageTypeName = imageTypeName + "tif";
            }
            if (imageTypeName.EndsWith("tiff", true, null))
            {
                imageTypeName = imageTypeName.Remove(imageTypeName.LastIndexOf("tiff"));
                imageTypeName = imageTypeName + "tif";
            }

            return imageTypeName;
        }

        /// <summary>
        /// Build a image size part of the web request.
        /// </summary>
        /// <param name="res"></param>
        /// <param name="boundingBox"></param>
        /// <returns></returns>
        private string ProcessImageSizeBody(int res, BoundingBox boundingBox)
        {
            double ratio = boundingBox.Diagonal.X / boundingBox.Diagonal.Y;

            var size = ratio > 1 ? ImageSizeBody(res, res / ratio) : ImageSizeBody(res * ratio, res);
            return size;
        }

        private string ImageSizeBody(double x, double y)
        {
            return $"&size={x}%2C{y}";
        }

        /// <summary>
        /// Build a part of the request body describing bounding box.
        /// </summary>
        /// <param name="boundingBox"></param>
        /// <returns></returns>
        private string BoundingBoxRequestString(BoundingBox boundingBox)
        {
            var min = boundingBox.Min;
            var max = boundingBox.Max;
            string body = $"bbox={min.X}%2C{min.Y}%2C{max.X}%2C{max.Y}";
            return body;
        }

        /// <summary>
        /// Build a string request body for the image query.
        /// </summary>
        /// <param name="url"></param> main url
        /// <param name="bbox"></param> bounding box for the image
        /// <param name="resolution"></param>
        /// <param name="heronSRS"></param>
        /// <param name="imageType"></param> file type
        /// <returns></returns>
        private string RequestBody(string url, BoundingBox bbox, int resolution, int heronSRS, string imageType)
        {
            var boundingBoxBody = BoundingBoxRequestString(bbox);
            var imageSizeBody = ProcessImageSizeBody(resolution, bbox);

            const string bboxSRName = "&bboxSR=";
            var bboxSrBody = $"{bboxSRName}{heronSRS}";

            const string imageSrName = "&imageSR=";
            var imageBody = $"{imageSrName}{heronSRS}";

            const string formatName = "&format=";
            var imageTypeBody = $"{formatName}{imageType}";

            string request = $"{url}{boundingBoxBody}{bboxSrBody}{imageSizeBody}{imageBody}{imageTypeBody}";
            return request;
        }


        private JObject rasterJson = JObject.Parse(Heron.Convert.GetEnpoints());

        /// <summary>
        /// Adds to the context menu an option to create a pre-populated list of common REST Raster sources
        /// </summary>
        /// <param name="menu"></param>
        /// https://discourse.mcneel.com/t/generated-valuelist-not-working/79406/6?u=hypar
        public override void AppendAdditionalMenuItems(ToolStripDropDown menu)
        {
            var rasterSourcesJson = rasterJson["REST Raster"].Select(x => x["source"]).Distinct();
            List<string> rasterSources = rasterSourcesJson.Values<string>().ToList();
            foreach (var src in rasterSourcesJson)
            {
                ToolStripMenuItem root = GH_DocumentObject.Menu_AppendItem(menu, "Create " + src.ToString() + " Source List", CreateRasterList);
                root.ToolTipText = "Click this to create a pre-populated list of some " + src.ToString() + " sources.";
                base.AppendAdditionalMenuItems(menu);
            }
        }

        /// <summary>
        /// Creates a value list pre-populated with possible accent colors and adds it to the Grasshopper Document, located near the component pivot.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="e">The <see cref="System.EventArgs"/> instance containing the event data.</param>
        private void CreateRasterList(object sender, System.EventArgs e)
        {
            string source = sender.ToString();
            source = source.Replace("Create ", "");
            source = source.Replace(" Source List", "");

            GH_DocumentIO docIO = new GH_DocumentIO();
            docIO.Document = new GH_Document();

            ///Initialize object
            GH_ValueList vl = new GH_ValueList();

            ///Clear default contents
            vl.ListItems.Clear();

            foreach (var service in rasterJson["REST Raster"])
            {
                if (service["source"].ToString() == source)
                {
                    GH_ValueListItem vi = new GH_ValueListItem(service["service"].ToString(), String.Format("\"{0}\"", service["url"].ToString()));
                    vl.ListItems.Add(vi);
                }
            }

            ///Set component nickname
            vl.NickName = source;

            ///Get active GH doc
            GH_Document doc = OnPingDocument();
            if (docIO.Document == null) return;

            ///Place the object
            docIO.Document.AddObject(vl, false, 1);

            ///Get the pivot of the "URL" param
            PointF currPivot = Params.Input[4].Attributes.Pivot;

            ///Set the pivot of the new object
            vl.Attributes.Pivot = new PointF(currPivot.X - 400, currPivot.Y - 11);

            docIO.Document.SelectAll();
            docIO.Document.ExpireSolution();
            docIO.Document.MutateAllIds();
            IEnumerable<IGH_DocumentObject> objs = docIO.Document.Objects;
            doc.DeselectAll();
            doc.UndoUtil.RecordAddObjectEvent("Create REST Raster Source List", objs);
            doc.MergeDocument(docIO.Document);
        }

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                return Properties.Resources.raster;
            }
        }

        public override Guid ComponentGuid
        {
            get { return new Guid("{dc88bb7f-7bd9-427a-8240-44286a6ca3c4}"); }
        }
    }
}
