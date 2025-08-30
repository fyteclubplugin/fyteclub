const AWS = require('aws-sdk');
const s3 = new AWS.S3();

exports.handler = async (event) => {
    const bucketName = process.env.MOD_BUCKET;
    const sizeLimitGB = parseFloat(process.env.SIZE_LIMIT_GB || '4.5');
    const sizeLimitBytes = sizeLimitGB * 1024 * 1024 * 1024;
    
    try {
        // Get bucket size
        const bucketSize = await getBucketSize(bucketName);
        console.log(`Bucket size: ${(bucketSize / 1024 / 1024 / 1024).toFixed(2)} GB`);
        
        if (bucketSize < sizeLimitBytes) {
            console.log('Bucket size under limit, no cleanup needed');
            return { statusCode: 200, body: 'No cleanup needed' };
        }
        
        console.log('Bucket approaching limit, starting cleanup...');
        
        // Get all objects sorted by last modified (oldest first)
        const objects = await getAllObjects(bucketName);
        objects.sort((a, b) => new Date(a.LastModified) - new Date(b.LastModified));
        
        let deletedSize = 0;
        const objectsToDelete = [];
        
        // Delete oldest objects until we're back under 4GB
        const targetSize = 4 * 1024 * 1024 * 1024; // 4GB
        for (const obj of objects) {
            if (bucketSize - deletedSize <= targetSize) break;
            
            objectsToDelete.push({ Key: obj.Key });
            deletedSize += obj.Size;
            
            // Delete in batches of 1000 (S3 limit)
            if (objectsToDelete.length >= 1000) {
                await deleteObjects(bucketName, objectsToDelete);
                console.log(`Deleted batch of ${objectsToDelete.length} objects`);
                objectsToDelete.length = 0;
            }
        }
        
        // Delete remaining objects
        if (objectsToDelete.length > 0) {
            await deleteObjects(bucketName, objectsToDelete);
            console.log(`Deleted final batch of ${objectsToDelete.length} objects`);
        }
        
        console.log(`Cleanup complete. Deleted ${(deletedSize / 1024 / 1024 / 1024).toFixed(2)} GB`);
        
        return {
            statusCode: 200,
            body: JSON.stringify({
                message: 'Cleanup completed',
                deletedSizeGB: (deletedSize / 1024 / 1024 / 1024).toFixed(2),
                remainingSizeGB: ((bucketSize - deletedSize) / 1024 / 1024 / 1024).toFixed(2)
            })
        };
        
    } catch (error) {
        console.error('Cleanup error:', error);
        return {
            statusCode: 500,
            body: JSON.stringify({ error: error.message })
        };
    }
};

async function getBucketSize(bucketName) {
    let totalSize = 0;
    let continuationToken = null;
    
    do {
        const params = {
            Bucket: bucketName,
            ContinuationToken: continuationToken
        };
        
        const response = await s3.listObjectsV2(params).promise();
        
        for (const obj of response.Contents || []) {
            totalSize += obj.Size;
        }
        
        continuationToken = response.NextContinuationToken;
    } while (continuationToken);
    
    return totalSize;
}

async function getAllObjects(bucketName) {
    const objects = [];
    let continuationToken = null;
    
    do {
        const params = {
            Bucket: bucketName,
            ContinuationToken: continuationToken
        };
        
        const response = await s3.listObjectsV2(params).promise();
        objects.push(...(response.Contents || []));
        continuationToken = response.NextContinuationToken;
    } while (continuationToken);
    
    return objects;
}

async function deleteObjects(bucketName, objects) {
    if (objects.length === 0) return;
    
    const params = {
        Bucket: bucketName,
        Delete: {
            Objects: objects,
            Quiet: true
        }
    };
    
    await s3.deleteObjects(params).promise();
}