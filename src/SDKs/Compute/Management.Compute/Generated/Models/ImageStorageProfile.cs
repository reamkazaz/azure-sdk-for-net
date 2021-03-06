// <auto-generated>
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for
// license information.
//
// Code generated by Microsoft (R) AutoRest Code Generator.
// Changes may cause incorrect behavior and will be lost if the code is
// regenerated.
// </auto-generated>

namespace Microsoft.Azure.Management.Compute.Models
{
    using Microsoft.Rest;
    using Newtonsoft.Json;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;

    /// <summary>
    /// Describes a storage profile.
    /// </summary>
    public partial class ImageStorageProfile
    {
        /// <summary>
        /// Initializes a new instance of the ImageStorageProfile class.
        /// </summary>
        public ImageStorageProfile()
        {
            CustomInit();
        }

        /// <summary>
        /// Initializes a new instance of the ImageStorageProfile class.
        /// </summary>
        /// <param name="osDisk">Specifies information about the operating
        /// system disk used by the virtual machine. &lt;br&gt;&lt;br&gt; For
        /// more information about disks, see [About disks and VHDs for Azure
        /// virtual
        /// machines](https://docs.microsoft.com/azure/virtual-machines/virtual-machines-windows-about-disks-vhds?toc=%2fazure%2fvirtual-machines%2fwindows%2ftoc.json).</param>
        /// <param name="dataDisks">Specifies the parameters that are used to
        /// add a data disk to a virtual machine. &lt;br&gt;&lt;br&gt; For more
        /// information about disks, see [About disks and VHDs for Azure
        /// virtual
        /// machines](https://docs.microsoft.com/azure/virtual-machines/virtual-machines-windows-about-disks-vhds?toc=%2fazure%2fvirtual-machines%2fwindows%2ftoc.json).</param>
        public ImageStorageProfile(ImageOSDisk osDisk, IList<ImageDataDisk> dataDisks = default(IList<ImageDataDisk>))
        {
            OsDisk = osDisk;
            DataDisks = dataDisks;
            CustomInit();
        }

        /// <summary>
        /// An initialization method that performs custom operations like setting defaults
        /// </summary>
        partial void CustomInit();

        /// <summary>
        /// Gets or sets specifies information about the operating system disk
        /// used by the virtual machine. &amp;lt;br&amp;gt;&amp;lt;br&amp;gt;
        /// For more information about disks, see [About disks and VHDs for
        /// Azure virtual
        /// machines](https://docs.microsoft.com/azure/virtual-machines/virtual-machines-windows-about-disks-vhds?toc=%2fazure%2fvirtual-machines%2fwindows%2ftoc.json).
        /// </summary>
        [JsonProperty(PropertyName = "osDisk")]
        public ImageOSDisk OsDisk { get; set; }

        /// <summary>
        /// Gets or sets specifies the parameters that are used to add a data
        /// disk to a virtual machine. &amp;lt;br&amp;gt;&amp;lt;br&amp;gt; For
        /// more information about disks, see [About disks and VHDs for Azure
        /// virtual
        /// machines](https://docs.microsoft.com/azure/virtual-machines/virtual-machines-windows-about-disks-vhds?toc=%2fazure%2fvirtual-machines%2fwindows%2ftoc.json).
        /// </summary>
        [JsonProperty(PropertyName = "dataDisks")]
        public IList<ImageDataDisk> DataDisks { get; set; }

        /// <summary>
        /// Validate the object.
        /// </summary>
        /// <exception cref="ValidationException">
        /// Thrown if validation fails
        /// </exception>
        public virtual void Validate()
        {
            if (OsDisk == null)
            {
                throw new ValidationException(ValidationRules.CannotBeNull, "OsDisk");
            }
            if (OsDisk != null)
            {
                OsDisk.Validate();
            }
            if (DataDisks != null)
            {
                foreach (var element in DataDisks)
                {
                    if (element != null)
                    {
                        element.Validate();
                    }
                }
            }
        }
    }
}
