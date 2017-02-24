using System.Runtime.Serialization;
using Newtonsoft.Json;

namespace StardewModdingAPI.Framework.Models
{
    /// <summary>Contains abstract metadata about an incompatible mod.</summary>
    internal class IncompatibleMod
    {
        /*********
        ** Accessors
        *********/
        /****
        ** From config
        ****/
        /// <summary>The unique mod ID.</summary>
        public string ID { get; set; }

        /// <summary>The mod name.</summary>
        public string Name { get; set; }

        /// <summary>The oldest incompatible mod version, or <c>null</c> for all past versions.</summary>
        public string LowerVersion { get; set; }

        /// <summary>The most recent incompatible mod version.</summary>
        public string UpperVersion { get; set; }

        /// <summary>The URL the user can check for an official updated version.</summary>
        public string UpdateUrl { get; set; }

        /// <summary>The URL the user can check for an unofficial updated version.</summary>
        public string UnofficialUpdateUrl { get; set; }

        /// <summary>A regular expression matching version strings to consider compatible, even if they technically precede <see cref="UpperVersion"/>.</summary>
        public string ForceCompatibleVersion { get; set; }

        /// <summary>The reason phrase to show in the warning, or <c>null</c> to use the default value.</summary>
        /// <example>"this version is incompatible with the latest version of the game"</example>
        public string ReasonPhrase { get; set; }


        /****
        ** Injected
        ****/
        /// <summary>The semantic version corresponding to <see cref="LowerVersion"/>.</summary>
        [JsonIgnore]
        public ISemanticVersion LowerSemanticVersion { get; set; }

        /// <summary>The semantic version corresponding to <see cref="UpperVersion"/>.</summary>
        [JsonIgnore]
        public ISemanticVersion UpperSemanticVersion { get; set; }


        /*********
        ** Private methods
        *********/
        /// <summary>The method called when the model finishes deserialising.</summary>
        /// <param name="context">The deserialisation context.</param>
        [OnDeserialized]
        private void OnDeserialized(StreamingContext context)
        {
            this.LowerSemanticVersion = this.LowerVersion != null ? new SemanticVersion(this.LowerVersion) : null;
            this.UpperSemanticVersion = this.UpperVersion != null ? new SemanticVersion(this.UpperVersion) : null;
        }
    }
}
