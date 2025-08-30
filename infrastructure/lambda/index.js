const AWS = require('aws-sdk');

const dynamodb = new AWS.DynamoDB.DocumentClient();
const s3 = new AWS.S3();

// Input validation helpers
function validatePlayerId(playerId) {
    if (!playerId || typeof playerId !== 'string') {
        throw new Error('Invalid player ID');
    }
    // Only allow alphanumeric characters and hyphens
    if (!/^[a-zA-Z0-9\-_]{1,64}$/.test(playerId)) {
        throw new Error('Player ID contains invalid characters');
    }
    return playerId;
}

function validateModId(modId) {
    if (!modId || typeof modId !== 'string') {
        throw new Error('Invalid mod ID');
    }
    // Only allow alphanumeric characters and hyphens
    if (!/^[a-zA-Z0-9\-_]{1,64}$/.test(modId)) {
        throw new Error('Mod ID contains invalid characters');
    }
    return modId;
}

function validateGroupId(groupId) {
    if (!groupId || typeof groupId !== 'string') {
        throw new Error('Invalid group ID');
    }
    // Only allow alphanumeric characters and hyphens
    if (!/^[a-zA-Z0-9\-_]{1,64}$/.test(groupId)) {
        throw new Error('Group ID contains invalid characters');
    }
    return groupId;
}

function sanitizeInput(input) {
    if (typeof input === 'string') {
        return input.replace(/[<>"'&]/g, '');
    }
    return input;
}

const PLAYERS_TABLE = process.env.PLAYERS_TABLE;
const MODS_TABLE = process.env.MODS_TABLE;
const GROUPS_TABLE = process.env.GROUPS_TABLE;
const MOD_BUCKET = process.env.MOD_BUCKET;

exports.handler = async (event) => {
    const { httpMethod, path, pathParameters, body } = event;
    
    try {
        let response;
        
        if (path.startsWith('/api/v1/players')) {
            response = await handlePlayerRequests(httpMethod, pathParameters, body);
        } else if (path.startsWith('/api/v1/mods')) {
            response = await handleModRequests(httpMethod, pathParameters, body);
        } else if (path.startsWith('/api/v1/groups')) {
            response = await handleGroupRequests(httpMethod, pathParameters, body);
        } else {
            response = {
                statusCode: 404,
                body: JSON.stringify({ error: 'Not found' })
            };
        }
        
        return {
            ...response,
            headers: {
                'Content-Type': 'application/json',
                'Access-Control-Allow-Origin': '*',
                'Access-Control-Allow-Methods': 'GET, POST, PUT, DELETE, OPTIONS',
                'Access-Control-Allow-Headers': 'Content-Type, Authorization'
            }
        };
        
    } catch (error) {
        console.error('Error:', error);
        return {
            statusCode: 500,
            headers: {
                'Content-Type': 'application/json',
                'Access-Control-Allow-Origin': '*'
            },
            body: JSON.stringify({ error: 'Internal server error' })
        };
    }
};

async function handlePlayerRequests(method, params, body) {
    const playerId = validatePlayerId(params?.playerId);
    
    switch (method) {
        case 'GET':
            const player = await dynamodb.get({
                TableName: PLAYERS_TABLE,
                Key: { player_id: playerId, sk: 'PROFILE' }
            }).promise();
            
            return {
                statusCode: 200,
                body: JSON.stringify(player.Item || {})
            };
            
        case 'POST':
            const playerData = JSON.parse(body);
            // Sanitize all string fields
            const sanitizedData = {};
            for (const [key, value] of Object.entries(playerData)) {
                sanitizedData[key] = sanitizeInput(value);
            }
            
            await dynamodb.put({
                TableName: PLAYERS_TABLE,
                Item: {
                    player_id: playerId,
                    sk: 'PROFILE',
                    ...sanitizedData,
                    last_updated: new Date().toISOString()
                }
            }).promise();
            
            return {
                statusCode: 200,
                body: JSON.stringify({ success: true })
            };
            
        default:
            return { statusCode: 405, body: JSON.stringify({ error: 'Method not allowed' }) };
    }
}

async function handleModRequests(method, params, body) {
    const modId = params?.modId ? validateModId(params.modId) : null;
    
    switch (method) {
        case 'GET':
            if (params?.action === 'download') {
                const url = s3.getSignedUrl('getObject', {
                    Bucket: MOD_BUCKET,
                    Key: `mods/${modId}`,
                    Expires: 3600
                });
                
                return {
                    statusCode: 200,
                    body: JSON.stringify({ download_url: url })
                };
            }
            
            const mod = await dynamodb.get({
                TableName: MODS_TABLE,
                Key: { mod_id: modId, sk: 'METADATA' }
            }).promise();
            
            return {
                statusCode: 200,
                body: JSON.stringify(mod.Item || {})
            };
            
        case 'POST':
            const modData = JSON.parse(body);
            const newModId = `mod_${Date.now()}`;
            
            // Sanitize mod data
            const sanitizedModData = {};
            for (const [key, value] of Object.entries(modData)) {
                sanitizedModData[key] = sanitizeInput(value);
            }
            
            await dynamodb.put({
                TableName: MODS_TABLE,
                Item: {
                    mod_id: newModId,
                    sk: 'METADATA',
                    ...sanitizedModData,
                    upload_date: new Date().toISOString()
                }
            }).promise();
            
            const uploadUrl = s3.getSignedUrl('putObject', {
                Bucket: MOD_BUCKET,
                Key: `mods/${newModId}`,
                Expires: 3600
            });
            
            return {
                statusCode: 200,
                body: JSON.stringify({ 
                    mod_id: newModId,
                    upload_url: uploadUrl
                })
            };
            
        default:
            return { statusCode: 405, body: JSON.stringify({ error: 'Method not allowed' }) };
    }
}

async function handleGroupRequests(method, params, body) {
    const groupId = validateGroupId(params?.groupId);
    
    switch (method) {
        case 'GET':
            const group = await dynamodb.get({
                TableName: GROUPS_TABLE,
                Key: { group_id: groupId, sk: 'INFO' }
            }).promise();
            
            return {
                statusCode: 200,
                body: JSON.stringify(group.Item || {})
            };
            
        case 'POST':
            if (params?.action === 'join') {
                const { player_id } = JSON.parse(body);
                const validatedPlayerId = validatePlayerId(player_id);
                
                await dynamodb.put({
                    TableName: GROUPS_TABLE,
                    Item: {
                        group_id: groupId,
                        sk: `MEMBER#${validatedPlayerId}`,
                        player_id: validatedPlayerId,
                        joined_date: new Date().toISOString()
                    }
                }).promise();
                
                return {
                    statusCode: 200,
                    body: JSON.stringify({ success: true })
                };
            }
            
            return { statusCode: 400, body: JSON.stringify({ error: 'Invalid request' }) };
            
        default:
            return { statusCode: 405, body: JSON.stringify({ error: 'Method not allowed' }) };
    }
}