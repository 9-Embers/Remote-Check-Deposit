using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text;

using com.bemaservices.RemoteCheckDeposit.Records.X9100;

using Rock;
using Rock.Model;

namespace com.bemaservices.RemoteCheckDeposit.FileFormatTypes
{
    /// <summary>
    /// Defines the basic functionality of any component that will be exporting using the X9.100
    /// DSTU standard.
    /// </summary>
    [Description( "Processes a batch export for Farmers State Bank.  This exports is built on X9.100-187 Standard " )]
    [Export(typeof(FileFormatTypeComponent))]
    [ExportMetadata("ComponentName", "Farmers State Bank")]

    class FarmersStateBank : X9100DSTU
    {
        #region Private Members

        #endregion

        #region System Setting Keys

        /// <summary>
        /// The system setting for the next cash header identifier. These should never be
        /// repeated. Ever.
        /// </summary>
        protected const string SystemSettingNextCashHeaderId = "FarmersStateBank.NextCashHeaderId";

        /// <summary>
        /// The system setting that contains the last file modifier we used.
        /// </summary>
        protected const string SystemSettingLastFileModifier = "FarmersStateBank.LastFileModifier";

        /// <summary>
        /// The last item sequence number used for items.
        /// </summary>
        protected const string LastItemSequenceNumberKey = "FarmersStateBank.LastItemSequenceNumber";

        #endregion

        #region Export Batches

        #endregion

        #region File Records

        /// <summary>
        /// Gets the file header record (type 01).
        /// </summary>
        /// <param name="options">Export options to be used by the component.</param>
        /// <returns>
        /// A FileHeader record.
        /// </returns>
        protected override FileHeader GetFileHeaderRecord( ExportOptions options )
        {
            var institutionRoutingNumber = Rock.Security.Encryption.DecryptString( GetAttributeValue( options.FileFormat, "InstitutionRoutingNumber" ) );

            var header = base.GetFileHeaderRecord( options );

            header.ImmediateOriginRoutingNumber = institutionRoutingNumber;
            header.StandardLevel = 03;

            //
            // The combination of the following fields must be unique:
            // DestinationRoutingNumber + OriginatingRoutingNumber + CreationDateTime + FileIdModifier
            //
            // If the last file we sent has the same routing numbers and creation date time then
            // increment the file id modifier.
            //
            var fileIdModifier = "A";
            header.CountryCode = string.Empty;
            var hashText = header.ImmediateDestinationRoutingNumber + header.ImmediateOriginRoutingNumber + header.FileCreationDateTime.ToString( "yyyyMMdd" );
            var hash = HashString( hashText );

            //
            // find the last modifier, if there was one.
            //
            var lastModifier = GetSystemSetting( SystemSettingLastFileModifier );
            if ( !string.IsNullOrWhiteSpace( lastModifier ) )
            {
                var components = lastModifier.Split( '|' );

                if ( components.Length == 2 )
                {
                    //
                    // If the modifier is for the same file, increment the file modifier.
                    //
                    if ( components[0] == hash )
                    {
                        fileIdModifier = ( ( char ) ( components[1][0] + 1 ) ).ToString();

                        //
                        // If we have done more than 26 files today, assume we are testing and start back at 'A'.
                        //
                        if ( fileIdModifier[0] > 'Z' )
                        {
                            fileIdModifier = "A";
                        }
                    }
                }
            }

            header.FileIdModifier = fileIdModifier;
            SetSystemSetting( SystemSettingLastFileModifier, string.Join( "|", hash, fileIdModifier ) );

            return header;
        }

        /// <summary>
        /// Gets the cash letter header record (type 10).
        /// </summary>
        /// <param name="options">Export options to be used by the component.</param>
        /// <returns>
        /// A CashLetterHeader record.
        /// </returns>
        protected override CashLetterHeader GetCashLetterHeaderRecord( ExportOptions options )
        {
            var header = base.GetCashLetterHeaderRecord( options );
            header.EceInstitutionRoutingNumber = header.DestinationRoutingNumber;
            header.FedWorkType = "C";

            int cashHeaderId = GetSystemSetting( SystemSettingNextCashHeaderId ).AsIntegerOrNull() ?? 1;
            header.CashLetterId = cashHeaderId.ToString( "D8" );
            SetSystemSetting( SystemSettingNextCashHeaderId, ( cashHeaderId + 1 ).ToString() );

            return header;
        }

        /// <summary>
        /// Gets the bundle header record (type 20).
        /// </summary>
        /// <param name="options">Export options to be used by the component.</param>
        /// <param name="bundleIndex">Number of existing bundle records in the cash letter.</param>
        /// <returns>A BundleHeader record.</returns>
        protected override BundleHeader GetBundleHeader(ExportOptions options, int bundleIndex)
        {
            var header = base.GetBundleHeader( options, bundleIndex );
            header.ReturnLocationRoutingNumber = string.Empty;
            header.SequenceNumber = header.SequenceNumber.PadLeft( 4, '0' );
            return header;
        }

        /// <summary>
        /// Gets the item detail records (type 25)
        /// </summary>
        /// <param name="options">Export options to be used by the component.</param>
        /// <param name="transaction">The transaction being deposited.</param>
        /// <returns>A collection of records.</returns>
        protected override List<Record> GetItemDetailRecords(ExportOptions options, FinancialTransaction transaction)
        {
            var records = base.GetItemDetailRecords( options, transaction );
            var sequenceNumber = GetNextItemSequenceNumber();

            var checkDetail = records.Where( r => r.RecordType == 25 ).Cast<CheckDetail>().FirstOrDefault();
            checkDetail.ClientInstitutionItemSequenceNumber = sequenceNumber.ToString( "000000000000000" );
            checkDetail.CheckDetailRecordAddendumCount = 0;

            //return records;
            // Type 26 is not needed
            return records.Where( r => r.RecordType == 25 ).ToList();
        }

        /// <summary>
        /// Gets the image records (type 50 and 52)
        /// </summary>
        /// <param name="options"></param>
        /// <param name="transaction"></param>
        /// <param name="image"></param>
        /// <param name="isFront"></param>
        /// <returns></returns>
        protected override List<Record> GetImageRecords( ExportOptions options, FinancialTransaction transaction, FinancialTransactionImage image, bool isFront )
        {
            var records = base.GetImageRecords( options, transaction, image, isFront );

            var detail = records.Where( r => r.RecordType == 50 ).Cast<ImageViewDetail>().FirstOrDefault();
            if ( detail != null )
            {
                detail.DigitalSignatureMethod = null;
                detail.SecurityKeySize = null;
                detail.StartOfProtectedData = null;
                detail.LengthOfProtectedData = null;
            }

            var data = records.Where( r => r.RecordType == 52 ).Cast<ImageViewData>().FirstOrDefault();
            if ( data != null )
            {
                data.ClientInstitutionItemSequenceNumber = GetSystemSetting( LastItemSequenceNumberKey ).PadLeft( 15, '0' );
                data.ClippingCoordinateH1 = null;
                data.ClippingCoordinateH2 = null;
                data.ClippingCoordinateV1 = null;
                data.ClippingCoordinateV2 = null;
            }

            return records;
        }

        /// <summary>
        /// Gets the bundle control record (type 70)
        /// </summary>
        /// <param name="options"></param>
        /// <param name="records"></param>
        /// <returns></returns>
        protected override BundleControl GetBundleControl( ExportOptions options, List<Record> records )
        {
            var control = base.GetBundleControl( options, records );
            control.MICRValidTotalAmount = null;
            return control;
        }

        /// <summary>
        /// Gets the Cash Leter Control Record (type 90)
        /// </summary>
        /// <param name="options"></param>
        /// <param name="records"></param>
        /// <returns></returns>
        protected override CashLetterControl GetCashLetterControlRecord( ExportOptions options, List<Record> records )
        {
            var control = base.GetCashLetterControlRecord( options, records );
            control.ECEInstitutionName = GetAttributeValue( options.FileFormat, "ContactName" );
            return control;
        }

        #endregion

        #region Methods

        /// <summary>
        /// Gets the next item sequence number.
        /// </summary>
        /// <returns>An integer that identifies the unique item sequence number that can be used.</returns>
        protected int GetNextItemSequenceNumber()
        {
            int lastSequence = GetSystemSetting(LastItemSequenceNumberKey).AsIntegerOrNull() ?? 0;
            int nextSequence = lastSequence + 1;

            SetSystemSetting(LastItemSequenceNumberKey, nextSequence.ToString());

            return nextSequence;
        }

        /// <summary>
        /// Hashes the string with SHA256.
        /// </summary>
        /// <param name="contents">The contents to be hashed.</param>
        /// <returns>A hex representation of the hash.</returns>
        protected string HashString( string contents )
        {
            byte[] byteContents = Encoding.Unicode.GetBytes( contents );

            var hash = new System.Security.Cryptography.SHA256CryptoServiceProvider().ComputeHash( byteContents );

            return string.Join( "", hash.Select( b => b.ToString( "x2" ) ).ToArray() );
        }

        #endregion

    }
}
