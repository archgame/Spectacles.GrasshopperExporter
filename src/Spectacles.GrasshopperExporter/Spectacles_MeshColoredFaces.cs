﻿//The MIT License (MIT)

//Copyright (c) 2015 Thornton Tomasetti

//Permission is hereby granted, free of charge, to any person obtaining a copy
//of this software and associated documentation files (the "Software"), to deal
//in the Software without restriction, including without limitation the rights
//to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
//copies of the Software, and to permit persons to whom the Software is
//furnished to do so, subject to the following conditions:

//The above copyright notice and this permission notice shall be included in all
//copies or substantial portions of the Software.

//THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
//IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
//FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
//AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
//LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
//OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
//SOFTWARE.

using System;
using System.Dynamic;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using System.Timers;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;
using Spectacles.GrasshopperExporter.Properties;

using Newtonsoft.Json;

namespace Spectacles.GrasshopperExporter
{
    public class Spectacles_MeshColoredFaces : GH_Component
    {
        /// <summary>
        /// Initializes a new instance of the Spectacles_coloredMesh class.
        /// </summary>
        public Spectacles_MeshColoredFaces()
            : base("Spectacles_MeshColoredFaces", "Spectacles_MeshColoredFaces",
                "Creates a Spectacles mesh and a set of materials from a grasshopper mesh and a list of colors - one color per face.  If the colors list isn't the same length as the list of faces, we'll do standard grasshopper longest list iteration, using the mesh faces as the driving list.",
                "TT Toolbox", "Spectacles")
        {
        }

        public override GH_Exposure Exposure
        {
            get
            {
                return GH_Exposure.primary;
            }
        }

        /// <summary>
        /// Registers all the input parameters for this component.
        /// </summary>
        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddMeshParameter("Mesh", "M", "A Grasshopper Mesh", GH_ParamAccess.item);
            pManager.AddColourParameter("colors", "C", "A list of colors - one per face.", GH_ParamAccess.list);
            pManager.AddTextParameter("Attribute Names", "[aN]", "Attribute Names", GH_ParamAccess.list);
            pManager[2].Optional = true;
            pManager.AddTextParameter("Attribute Values", "[aV]", "Attribute Values", GH_ParamAccess.list);
            pManager[3].Optional = true;
            pManager.AddTextParameter("Layer", "[L]", "Layer", GH_ParamAccess.item);
            pManager[4].Optional = true;
        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter("Mesh Element", "Me", "Mesh Element output to feed into scene compiler component", GH_ParamAccess.item);
            //pManager.AddGenericParameter("Mesh Material", "Mm", "Mesh Material to feed into the Spectacles Mesh component.  Make sure to amtch this material with the corresponding mesh from Mj above.", GH_ParamAccess.item);
        }

        /// <summary>
        /// This is the method that actually does the work.
        /// </summary>
        /// <param name="DA">The DA object is used to retrieve from inputs and store in outputs.</param>
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            //local varaibles
            GH_Mesh mesh = null;
            List<GH_Colour> colors = new List<GH_Colour>();
            List<GH_String> attributeNames = new List<GH_String>();
            List<GH_String> attributeValues = new List<GH_String>();
            Dictionary<string, object> attributesDict = new Dictionary<string, object>();
            Layer layer = null;
            string layerName = "Default";
            //catch inputs and populate local variables
            if (!DA.GetData(0, ref mesh))
            {
                return;
            }
            if (mesh == null)
            {
                return;
            }
            if (!DA.GetDataList(1, colors))
            {
                return;
            }

            
            DA.GetDataList(2, attributeNames);
            DA.GetDataList(3, attributeValues);
            if (attributeValues.Count != attributeNames.Count)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Please provide equal numbers of attribute names and values.");
                return;
            }

            DA.GetData(4, ref layerName);

            layer = new Layer(layerName);

            //populate dictionary
            int i = 0;
            foreach (var a in attributeNames)
            {
                attributesDict.Add(a.Value, attributeValues[i].Value);
                i++;
            }

            //add the layer name to the attributes dictionary
            attributesDict.Add("layer", layerName);

            //create MeshFaceMaterial and assign mesh face material indexes in the attributes dict
            string meshMaterailJSON = makeMeshFaceMaterialJSON(mesh.Value, attributesDict, colors);

            //create json from mesh
            string meshJSON = _Utilities.geoJSON(mesh.Value, attributesDict);

            Material material = new Material(meshMaterailJSON, SpectaclesMaterialType.Mesh);
            Element e = new Element(meshJSON, SpectaclesElementType.Mesh, material, layer);

            DA.SetData(0, e);
           
        }

        private string makeMeshFaceMaterialJSON(Mesh mesh, Dictionary<string, object> attributesDict, List<GH_Colour> colors)
        {
            //JSON object to populate
            dynamic jason = new ExpandoObject();
            jason.uuid = Guid.NewGuid();
            jason.type = "MeshFaceMaterial"; 

            //we need an list of material indexes, one for each face of the mesh.  This will be stroed as a CSV string in the attributes dict
            //and on the viewer side we'll use this to set each mesh face's material index property
            List<int> myMaterialIndexes = new List<int>();

            //since some faces might share a material, we'll keep a local dict of materials to avoid duplicates
            //key = hex color, value = int representing a material index
            Dictionary<string, int> faceMaterials = new Dictionary<string, int>();

            //we'll loop over the mesh to make sure that each quad is assigned two material indexes
            //since it is really two triangles as a three.js mesh.  If there are fewer colors than mesh faces, we'll take the last material
            int matCounter = 0;
            int uniqueColorCounter = 0;
            foreach (var f in mesh.Faces)
            {
                //make sure there is an item at this index.  if not, grab the last one
                if (matCounter == mesh.Faces.Count)
                {
                    matCounter = mesh.Faces.Count = 1;
                }
                if (matCounter > colors.Count - 1) matCounter = colors.Count - 1;

                //get a string representation of the color
                string myColorStr = _Utilities.hexColor(colors[matCounter]);

                //check to see if we need to create a new material index
                if (!faceMaterials.ContainsKey(myColorStr))
                {
                    //add the color/index pair to our dictionary and increment the unique color counter
                    faceMaterials.Add(myColorStr, uniqueColorCounter);
                    uniqueColorCounter++;
                }

                //add the color[s] to the array.  one for a tri, two for a quad
                if (f.IsTriangle)
                {
                    myMaterialIndexes.Add(faceMaterials[myColorStr]);
                }
                if (f.IsQuad)
                {
                    myMaterialIndexes.Add(faceMaterials[myColorStr]);
                    myMaterialIndexes.Add(faceMaterials[myColorStr]);
                } 
                matCounter++;
            }

            //now that we know how many unique materials we need, we'll create a materials array on jason, and add them all to it
            jason.materials = new object[faceMaterials.Count];
            for (int i = 0; i < faceMaterials.Count; i++)
            {
                dynamic matthew = new ExpandoObject();
                matthew.uuid = Guid.NewGuid();
                matthew.type = "MeshBasicMaterial";
                matthew.side = 2;
                matthew.color = faceMaterials.Keys.ToList()[i];
                jason.materials[i] = matthew;
            }

            //finally, we need to add a csv string of the materials to our attribute dictionary
            attributesDict.Add("Spectacles_FaceColorIndexes", createCsvString(myMaterialIndexes));
            return JsonConvert.SerializeObject(jason);
        }

        //method to create a csv string out of a list of integers
        private object createCsvString(List<int> myMaterialIndexes)
        {
            string csv = "";
            foreach (var i in myMaterialIndexes)
            {
                csv = csv + i.ToString() + ",";
            }
            return csv;
        }

        /// <summary>
        /// Provides an Icon for the component.
        /// </summary>
        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                //You can add image files to your project resources and access them like this:
                return Resources.MESH_FACES;
            }
        }

        /// <summary>
        /// Gets the unique ID for this component. Do not change this ID after release.
        /// </summary>
        public override Guid ComponentGuid
        {
            get { return new Guid("{5ebd3604-91d6-4312-9650-cee7feda73cc}"); }
        }
    }
}