﻿using System;
using ClassicalSharp.GraphicsAPI;
using OpenTK;

namespace ClassicalSharp {
	
	
	// TODO: optimise chunk rendering
	//  --> reduce the two passes: liquid pass only needs 1 small texture
	//    |--> divide into 'solid', 'transparent', 'translucent passes'
	//       |--> don't render solid back facing polygons. may help with low performance computers.
	// |--> use indices.
	public class MapRenderer : IDisposable {
		
		struct Point3S {
			
			public short X, Y, Z;
			
			public Point3S( int x, int y, int z ) {
				X = (short)x;
				Y = (short)y;
				Z = (short)z;
			}
			
			public override string ToString() {
				return X + "," + Y + "," + Z;
			}
		}
		
		class ChunkInfo {
			
			public Point3S Location;
			
			public bool Visible = true;
			
			public ChunkDrawInfo[] DrawInfo;
			
			public ChunkInfo( int x, int y, int z ) {
				Location = new Point3S( x, y, z );
			}
		}
		
		public Game Window;
		public IGraphicsApi Graphics;
		
		int _1Dcount = 1;
		int total1DCount = 1;
		ChunkMeshBuilder builder;
		
		int width, height, length;
		ChunkInfo[] chunks;
		Vector3I chunkPos = new Vector3I( int.MaxValue, int.MaxValue, int.MaxValue );
		
		public readonly bool UsesLighting;
		int elementsPerBitmap = 0;
		
		public MapRenderer( Game window ) {
			Window = window;
			_1Dcount = window.TerrainAtlas1DTexIds.Length;
			total1DCount = _1Dcount * 2;
			builder = new ChunkMeshBuilderTex2Col4( window );
			UsesLighting = builder.UsesLighting;
			Graphics = window.Graphics;
			elementsPerBitmap = window.TerrainAtlas1D.elementsPerBitmap;
			Window.TerrainAtlasChanged += TerrainAtlasChanged;
			Window.OnNewMap += OnNewMap;
			Window.OnNewMapLoaded += OnNewMapLoaded;
			Window.EnvVariableChanged += EnvVariableChanged;
		}
		
		public void Dispose() {
			ClearChunkCache();
			chunks = null;
			Window.OnNewMap -= OnNewMap;
			Window.OnNewMapLoaded -= OnNewMapLoaded;
			Window.EnvVariableChanged -= EnvVariableChanged;
		}
		
		public void Refresh() {
			if( chunks != null && !Window.Map.IsNotLoaded ) {
				ClearChunkCache();
				CreateChunkCache();
			}
		}
		
		void EnvVariableChanged( object sender, EnvVariableEventArgs e ) {
			if( ( e.Variable == EnvVariable.SunlightColour ||
			     e.Variable == EnvVariable.ShadowlightColour ) && UsesLighting ) {
				Refresh();
			}
		}

		void TerrainAtlasChanged( object sender, EventArgs e ) {
			_1Dcount = Window.TerrainAtlas1DTexIds.Length;
			total1DCount = _1Dcount * 2;
			bool fullResetRequired = elementsPerBitmap != Window.TerrainAtlas1D.elementsPerBitmap;
			if( fullResetRequired ) {
				Refresh();
			}
			elementsPerBitmap = Window.TerrainAtlas1D.elementsPerBitmap;
		}
		
		void OnNewMap( object sender, EventArgs e ) {
			Window.ChunkUpdates = 0;
			ClearChunkCache();
			chunks = null;
			chunkPos = new Vector3I( int.MaxValue, int.MaxValue, int.MaxValue );
			builder.OnNewMap();
		}
		
		int chunksX, chunksY, chunksZ;
		void OnNewMapLoaded( object sender, EventArgs e ) {
			width = NextMultipleOf16( Window.Map.Width );
			height = NextMultipleOf16( Window.Map.Height );
			length = NextMultipleOf16( Window.Map.Length );
			chunksX = width >> 4;
			chunksY = height >> 4;
			chunksZ = length >> 4;
			
			chunks = new ChunkInfo[chunksX * chunksY * chunksZ];
			distances = new int[chunks.Length];
			CreateChunkCache();
			builder.OnNewMapLoaded();
		}
		
		void ClearChunkCache() {
			if( chunks == null ) return;
			for( int i = 0; i < chunks.Length; i++ ) {
				ChunkInfo info = chunks[i];
				ChunkDrawInfo[] drawInfoArray = info.DrawInfo;
				if( drawInfoArray == null ) continue;
				
				for( int j = 0; j < drawInfoArray.Length; j++ ) {
					ChunkDrawInfo drawInfo = drawInfoArray[j];
					if( drawInfo.VerticesCount > 0 && drawInfo.VboID != -1 ) {
						Graphics.DeleteVb( drawInfo.VboID );
					}
				}
			}
		}
		
		void CreateChunkCache() {
			int index = 0;
			for( int z = 0; z < length; z += 16 ) {
				for( int y = 0; y < height; y += 16 ) {
					for( int x = 0; x < width; x += 16 ) {
						chunks[index++] = new ChunkInfo( x, y, z );
					}
				}
			}
		}
		
		static int NextMultipleOf16( int value ) {
			int remainder = value % 16;
			return remainder == 0 ? value : value + 16 - remainder;
		}
		
		public void RedrawBlock( int x, int y, int z, byte block, int oldHeight, int newHeight ) {
			int cx = x >> 4;
			int cy = y >> 4;
			int cz = z >> 4;
			// NOTE: It's a lot faster to only update the chunks that are affected by the change in shadows,
			// rather than the entire column.
			int newLightcy = newHeight == -1 ? 0 : newHeight >> 4;
			int oldLightcy = oldHeight == -1 ? 0 : oldHeight >> 4;
			
			ResetChunkAndBelow( cx, cy, cz, newLightcy, oldLightcy );
			int bX = x & 0x0F; // % 16
			int bY = y & 0x0F;
			int bZ = z & 0x0F;
			
			if( bX == 0 && cx > 0 ) ResetChunkAndBelow( cx - 1, cy, cz, newLightcy, oldLightcy );
			if( bY == 0 && cy > 0 ) ResetChunkAndBelow( cx, cy - 1, cz, newLightcy, oldLightcy );
			if( bZ == 0 && cz > 0 ) ResetChunkAndBelow( cx, cy, cz - 1, newLightcy, oldLightcy );
			if( bX == 15 && cx < chunksX - 1 ) ResetChunkAndBelow( cx + 1, cy, cz, newLightcy, oldLightcy );
			if( bY == 15 && cy < chunksY - 1 ) ResetChunkAndBelow( cx, cy + 1, cz, newLightcy, oldLightcy );
			if( bZ == 15 && cz < chunksZ - 1 ) ResetChunkAndBelow( cx, cy, cz + 1, newLightcy, oldLightcy );
		}
		
		void ResetChunkAndBelow( int cx, int cy, int cz, int newLightCy, int oldLightCy ) {
			if( UsesLighting ) {
				if( newLightCy == oldLightCy ) {
					ResetChunk( cx, cy, cz );
				} else {
					int cyMax = Math.Max( newLightCy, oldLightCy );
					int cyMin = Math.Min( oldLightCy, newLightCy );
					for( cy = cyMax; cy >= cyMin; cy-- ) {
						ResetChunk( cx, cy, cz );
					}
				}
			} else {
				ResetChunk( cx, cy, cz );
			}
		}
		
		void ResetChunk( int cx, int cy, int cz ) {
			ChunkInfo info = null;
			for( int i = 0; i < chunks.Length; i++ ) {
				info = chunks[i];
				Point3S loc = info.Location;
				int locCx = loc.X >> 4;
				int locCy = loc.Y >> 4;
				int locCz = loc.Z >> 4;
				if( locCx == cx && locCy == cy && locCz == cz ) break;
			}
			ChunkDrawInfo[] drawInfoArray = info.DrawInfo;
			if( drawInfoArray == null ) return;
			
			for( int i = 0; i < drawInfoArray.Length; i++ ) {
				ChunkDrawInfo drawInfo = drawInfoArray[i];
				if( drawInfo.VerticesCount > 0 && drawInfo.VboID != -1 ) {
					Graphics.DeleteVb( drawInfo.VboID );
				}
				drawInfo.VboID = -1;
				drawInfoArray[i] = drawInfo;
			}
			info.DrawInfo = null;
		}
		
		public void Render( double deltaTime ) {
			if( chunks == null ) return;
			Window.Vertices = 0;
			UpdateSortOrder();
			UpdateChunks();
			
			// Render solid and fully transparent to fill depth buffer.
			// These blocks are treated as having an alpha value of either none or full.
			builder.BeginRender();
			Graphics.Texturing = true;
			Graphics.AlphaTest = true;
			for( int batch = 0; batch < _1Dcount; batch++ ) {
				Graphics.Bind2DTexture( Window.TerrainAtlas1DTexIds[batch] );
				RenderBatch( batch );
			}
			Graphics.AlphaTest = false;
			Graphics.Texturing = false;
			builder.EndRender();
			Window.MapEnvRenderer.RenderMapSides( deltaTime );
			Window.MapEnvRenderer.RenderMapEdges( deltaTime );
			
			// Render translucent(liquid) blocks. These 'blend' into other blocks.
			builder.BeginRender();
			Graphics.AlphaTest = false;
			Graphics.Texturing = false;
			Graphics.AlphaBlending = false;
			
			// First fill depth buffer
			Graphics.DepthTestFunc( DepthFunc.LessEqual );
			Graphics.ColourMask( false, false, false, false );
			for( int batch = _1Dcount; batch < total1DCount; batch++ ) {
				RenderBatchNoAdd( batch );
			}
			// Then actually draw the transluscent blocks
			Graphics.AlphaBlending = true;
			Graphics.Texturing = true;
			Graphics.ColourMask( true, true, true, true );
			for( int batch = _1Dcount; batch < total1DCount; batch++ ) {
				Graphics.Bind2DTexture( Window.TerrainAtlas1DTexIds[batch - _1Dcount] );
				RenderBatch( batch );
			}
			Graphics.DepthTestFunc( DepthFunc.Less );
			
			Graphics.AlphaTest = false;
			Graphics.AlphaBlending = false;
			Graphics.Texturing = false;
			builder.EndRender();
		}

		int[] distances;	
		void UpdateSortOrder() {
			Player p = Window.LocalPlayer;
			Vector3I newChunkPos = Vector3I.Floor( p.Position );
			newChunkPos.X = ( newChunkPos.X & ~0x0F ) + 8;
			newChunkPos.Y = ( newChunkPos.Y & ~0x0F ) + 8;
			newChunkPos.Z = ( newChunkPos.Z & ~0x0F ) + 8;
			if( newChunkPos != chunkPos ) {
				chunkPos = newChunkPos;
				for( int i = 0; i < distances.Length; i++ ) {
					ChunkInfo info = chunks[i];
					Point3S loc = info.Location;
					distances[i] = Utils.DistanceSquared( loc.X + 8, loc.Y + 8, loc.Z + 8, chunkPos.X, chunkPos.Y, chunkPos.Z );
				}
				// NOTE: Over 5x faster compared to normal comparison of IComparer<ChunkInfo>.Compare
				Array.Sort( distances, chunks );
			}
		}
		
		void UpdateChunks() {				
			int chunksUpdatedThisFrame = 0;
			int adjViewDistSqr = ( Window.ViewDistance + 14 ) * ( Window.ViewDistance + 14 );
			for( int i = 0; i < chunks.Length; i++ ) {
				ChunkInfo info = chunks[i];
				Point3S loc = info.Location;
				int distSqr = distances[i];
				bool inRange = distSqr <= adjViewDistSqr;
				
				if( info.DrawInfo == null ) {
					if( inRange && chunksUpdatedThisFrame < 4 ) {
						Window.ChunkUpdates++;
						info.DrawInfo = builder.GetDrawInfo( loc.X, loc.Y, loc.Z );
						chunksUpdatedThisFrame++;
					}
				}
					
				if( !inRange ) {
					info.Visible = false;
				} else {
					info.Visible = Window.Culling.SphereInFrustum( loc.X + 8, loc.Y + 8, loc.Z + 8, 14 ); // 14 ~ sqrt(3 * 8^2)
				}
			}
		}
		
		void RenderBatch( int batch ) {
			for( int i = 0; i < chunks.Length; i++ ) {
				ChunkInfo info = chunks[i];
				if( info.DrawInfo == null || !info.Visible ) continue;

				ChunkDrawInfo drawInfo = info.DrawInfo[batch];
				if( drawInfo.VerticesCount == 0 ) continue;
				
				builder.Render( drawInfo );
				Window.Vertices += drawInfo.VerticesCount;
			}
		}

		void RenderBatchNoAdd( int batch ) {
			for( int i = 0; i < chunks.Length; i++ ) {
				ChunkInfo info = chunks[i];
				if( info.DrawInfo == null || !info.Visible ) continue;

				ChunkDrawInfo drawInfo = info.DrawInfo[batch];
				if( drawInfo.VerticesCount == 0 ) continue;
				
				builder.Render( drawInfo );
			}
		}
	}
}